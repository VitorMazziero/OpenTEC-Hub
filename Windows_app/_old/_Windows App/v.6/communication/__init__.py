"""
communication package
~~~~~~~~~~~~~~~~~~~~~
Refactored transport + data-parsing + connection management for TECNAL controller.
"""
from .transport import (
    TransportLayer,
    USBTransport, USBConfig,
    WiFiTransport, WiFiConfig,
)
from .data_parser import DataParser, ParserConfig, SpikeFilter, SpikeFilterConfig, SensorReadings
from .connection_manager import ConnectionManager, ConnectionState

__all__ = [
    "TransportLayer",
    "USBTransport", "USBConfig",
    "WiFiTransport", "WiFiConfig",
    "DataParser", "ParserConfig",
    "SpikeFilter", "SpikeFilterConfig",
    "SensorReadings",
    "ConnectionManager", "ConnectionState",
]