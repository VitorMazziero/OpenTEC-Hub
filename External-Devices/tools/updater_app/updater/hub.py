"""
hub.py -- read the Hub's node directory to locate the external devices.

The Hub answers ``GET /nodes`` with the 10.1 document::

    {"hub_time_ms":123,"nodes":[{"dev":"pump","ip":"192.168.4.12","mac":"...",
     "version":"3.12","online":true,"age_ms":740,"registered":true,
     "last_hello_ms":...,"last_data_ms":...}]}

Freshness is computed from ``hub_time_ms`` minus ``last_*_ms`` rather than from
``age_ms``: the Hub fills ``age_ms`` with the sentinel 999999 when it has never
heard from a node, which is a marker and not a duration.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

import requests

# The Hub reports a node it never heard from with this placeholder address.
UNSET_IP = "0.0.0.0"

# Sentinel the Hub writes into age_ms when no contact was ever recorded.
AGE_SENTINEL_MS = 999999


class HubUnavailable(RuntimeError):
    """The Hub did not answer /nodes, so no node address could be discovered."""


@dataclass(frozen=True)
class NodeStatus:
    dev: str
    ip: str
    mac: str
    version: str
    online: bool
    registered: bool
    age_ms: Optional[int]

    @property
    def has_address(self) -> bool:
        return bool(self.ip) and self.ip != UNSET_IP

    @property
    def age_text(self) -> str:
        if self.age_ms is None:
            return "--"
        if self.age_ms < 1000:
            return str(self.age_ms) + " ms"
        if self.age_ms < 60000:
            return "{:.1f} s".format(self.age_ms / 1000)
        return "{:.1f} min".format(self.age_ms / 60000)


def _freshness_ms(node: dict, hub_time_ms: Optional[int]) -> Optional[int]:
    """Age of the most recent contact, or None when the Hub never heard the node."""
    last_seen = max(
        int(node.get("last_hello_ms") or 0),
        int(node.get("last_data_ms") or 0),
    )
    if last_seen <= 0:
        # Pre-10.1 Hub: fall back to age_ms, discarding the sentinel.
        reported = node.get("age_ms")
        if reported is None or int(reported) >= AGE_SENTINEL_MS:
            return None
        return int(reported)
    if hub_time_ms is None:
        return None
    return max(0, int(hub_time_ms) - last_seen)


def fetch_nodes(hub_ip: str, timeout: float = 2.0) -> dict:
    """Return the Hub's node directory keyed by device name.

    Raises HubUnavailable when the Hub cannot be reached or answers something
    that is not the expected document; the caller then falls back to the
    per-device access-point addresses.
    """
    url = "http://" + hub_ip + "/nodes"
    try:
        response = requests.get(url, timeout=timeout)
        response.raise_for_status()
        payload = response.json()
    except requests.RequestException as exc:
        raise HubUnavailable(url + ": " + str(exc)) from exc
    except ValueError as exc:
        raise HubUnavailable(url + ": resposta nao e JSON (" + str(exc) + ")") from exc

    if not isinstance(payload, dict) or "nodes" not in payload:
        raise HubUnavailable(url + ": resposta sem o campo 'nodes'")

    hub_time_ms = payload.get("hub_time_ms")
    hub_time_ms = int(hub_time_ms) if hub_time_ms is not None else None

    statuses = {}
    for node in payload.get("nodes") or []:
        if not isinstance(node, dict) or not node.get("dev"):
            continue
        key = str(node["dev"])
        statuses[key] = NodeStatus(
            dev=key,
            ip=str(node.get("ip") or ""),
            mac=str(node.get("mac") or ""),
            version=str(node.get("version") or ""),
            online=bool(node.get("online")),
            registered=bool(node.get("registered")),
            age_ms=_freshness_ms(node, hub_time_ms),
        )
    return statuses


def probe_update_route(ip: str, timeout: float = 2.0) -> bool:
    """True when the device serves GET /update, i.e. OTA is reachable right now."""
    try:
        response = requests.get("http://" + ip + "/update", timeout=timeout)
        return response.status_code < 400
    except requests.RequestException:
        return False
