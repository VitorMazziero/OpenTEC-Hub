#include <stdio.h>
#include <string.h>
#include <inttypes.h>
#include <errno.h>
#include <stdbool.h>

#include "esp_system.h"
#include "esp_log.h"
#include "esp_err.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"

#include "nvs_flash.h"
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"

#include "usb/usb_host.h"
#include "usb/cdc_acm_host.h"

#include "esp_timer.h"
#include "lwip/sockets.h"
#include "lwip/netdb.h"

/*---------------------------------------------------------------
 * Configuration
 *-------------------------------------------------------------*/
#define TAG                         "USB-AP-TCP"

#define EXAMPLE_USB_HOST_PRIORITY   (20)
#define EXAMPLE_USB_DEVICE_VID      (0x303A) // TinyUSB example vendor ID
#define EXAMPLE_USB_DEVICE_PID      (0x4001) // TinyUSB CDC device
#define EXAMPLE_USB_DEVICE_DUAL_PID (0x4002)

#define EXAMPLE_TX_TIMEOUT_MS       (1000)
#define RESPONSE_BUF_SIZE           (256)

// Wi-Fi AP settings
#define AP_SSID                     "Modulo TECNAL"
#define AP_PASS                     "Modulo TECNAL"
#define AP_CHANNEL                  1
#define MAX_STA_CONN                4

// TCP Server settings
#define TCP_SERVER_PORT             3333
#define MAX_CLIENTS                 4   // Maximum simultaneous clients
#define TCP_RX_BUFFER_SIZE          256
#define TCP_TX_BUFFER_SIZE          256

/*---------------------------------------------------------------
 * Global Variables
 *-------------------------------------------------------------*/
// USB CDC device handle (protected by usb_dev_mutex)
static cdc_acm_dev_hdl_t g_cdc_dev    = NULL;
static SemaphoreHandle_t usb_dev_mutex = NULL;

// Signaled when the USB CDC device is disconnected
static SemaphoreHandle_t device_disconnected_sem = NULL;

// Global response buffer for command/response exchange
static char  global_response_buf[RESPONSE_BUF_SIZE];
static size_t global_response_len = 0;
static SemaphoreHandle_t response_sem   = NULL; // Signaled when newline is detected
static SemaphoreHandle_t response_mutex = NULL; // Protects global_response_buf

// TCP client list management
static int client_sockets[MAX_CLIENTS] = { -1, -1, -1, -1 };
static SemaphoreHandle_t client_list_mutex = NULL; // Protects client_sockets

/*---------------------------------------------------------------
 * USB CDC Callbacks and Tasks
 *-------------------------------------------------------------*/
/**
 * @brief Called when data arrives from the USB CDC device.
 *
 * Data is appended to the global response buffer. If a newline is detected,
 * the response semaphore is given.
 */
static bool handle_rx(const uint8_t *data, size_t data_len, void *arg)
{
    ESP_LOGI(TAG, "Received %u bytes from USB CDC device", (unsigned)data_len);
    ESP_LOG_BUFFER_HEXDUMP(TAG, data, data_len, ESP_LOG_INFO);

    xSemaphoreTake(response_mutex, portMAX_DELAY);
    if ((global_response_len + data_len) < RESPONSE_BUF_SIZE) {
        memcpy(&global_response_buf[global_response_len], data, data_len);
        global_response_len += data_len;
        global_response_buf[global_response_len] = '\0';
        // If a newline is present, signal that the response is complete.
        if (strchr(global_response_buf, '\n') != NULL) {
            xSemaphoreGive(response_sem);
        }
    } else {
        ESP_LOGW(TAG, "Response buffer overflow, discarding...");
    }
    xSemaphoreGive(response_mutex);

    return true;
}

/**
 * @brief Handles USB CDC events (errors, disconnections, etc.).
 */
static void handle_event(const cdc_acm_host_dev_event_data_t *event, void *user_ctx)
{
    switch (event->type) {
    case CDC_ACM_HOST_ERROR:
        ESP_LOGE(TAG, "CDC-ACM error occurred, err_no=%i", event->data.error);
        break;
    case CDC_ACM_HOST_DEVICE_DISCONNECTED:
        ESP_LOGI(TAG, "USB CDC device disconnected");
        ESP_ERROR_CHECK(cdc_acm_host_close(event->data.cdc_hdl));
        xSemaphoreGive(device_disconnected_sem);
        xSemaphoreTake(usb_dev_mutex, portMAX_DELAY);
        g_cdc_dev = NULL;
        xSemaphoreGive(usb_dev_mutex);
        break;
    case CDC_ACM_HOST_SERIAL_STATE:
        ESP_LOGI(TAG, "Serial state notif: 0x%04X", event->data.serial_state.val);
        break;
    default:
        ESP_LOGW(TAG, "Unsupported CDC event: %d", event->type);
        break;
    }
}

/**
 * @brief USB Host library events task.
 */
static void usb_lib_task(void *arg)
{
    while (1) {
        uint32_t event_flags;
        usb_host_lib_handle_events(portMAX_DELAY, &event_flags);
        if (event_flags & USB_HOST_LIB_EVENT_FLAGS_NO_CLIENTS) {
            ESP_ERROR_CHECK(usb_host_device_free_all());
        }
        if (event_flags & USB_HOST_LIB_EVENT_FLAGS_ALL_FREE) {
            ESP_LOGI(TAG, "USB: All devices freed");
        }
    }
}

/**
 * @brief Task that continuously tries to open the USB CDC device if not already open.
 */
static void usb_cdc_task(void *arg)
{
    const cdc_acm_host_device_config_t dev_config = {
        .connection_timeout_ms = 1000,
        .out_buffer_size       = 512,
        .in_buffer_size        = 512,
        .user_arg              = NULL,
        .event_cb              = handle_event,
        .data_cb               = handle_rx
    };

    while (1) {
        xSemaphoreTake(usb_dev_mutex, portMAX_DELAY);
        bool device_open = (g_cdc_dev != NULL);
        xSemaphoreGive(usb_dev_mutex);

        if (!device_open) {
            cdc_acm_dev_hdl_t cdc_dev = NULL;
            ESP_LOGI(TAG, "Trying to open CDC device: VID=0x%04X, PID=0x%04X",
                     EXAMPLE_USB_DEVICE_VID, EXAMPLE_USB_DEVICE_PID);
            esp_err_t err = cdc_acm_host_open(EXAMPLE_USB_DEVICE_VID,
                                              EXAMPLE_USB_DEVICE_PID,
                                              0,
                                              &dev_config,
                                              &cdc_dev);
            if (err != ESP_OK) {
                ESP_LOGI(TAG, "Trying CDC device: VID=0x%04X, PID=0x%04X",
                         EXAMPLE_USB_DEVICE_VID, EXAMPLE_USB_DEVICE_DUAL_PID);
                err = cdc_acm_host_open(EXAMPLE_USB_DEVICE_VID,
                                        EXAMPLE_USB_DEVICE_DUAL_PID,
                                        0,
                                        &dev_config,
                                        &cdc_dev);
            }
            if (err == ESP_OK) {
                ESP_LOGI(TAG, "CDC device opened successfully.");
                xSemaphoreTake(usb_dev_mutex, portMAX_DELAY);
                g_cdc_dev = cdc_dev;
                xSemaphoreGive(usb_dev_mutex);
            } else {
                ESP_LOGW(TAG, "Failed to open CDC device, retrying...");
            }
        }
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

/*---------------------------------------------------------------
 * USB CDC Command/Response
 *-------------------------------------------------------------*/
/**
 * @brief Sends a command to the CDC device and waits for a newline-terminated response.
 *
 * The global response buffer is cleared, the command is sent (with a trailing newline if needed),
 * and the task waits (up to timeout_ms) until the response is complete.
 */
static esp_err_t send_command_sync(cdc_acm_dev_hdl_t cdc_dev,
                                   const char *cmd,
                                   char *out_resp,
                                   size_t out_len,
                                   uint32_t timeout_ms)
{
    char cmd_buf[128];
    if (strchr(cmd, '\n') == NULL) {
        snprintf(cmd_buf, sizeof(cmd_buf), "%s\n", cmd);
    } else {
        snprintf(cmd_buf, sizeof(cmd_buf), "%s", cmd);
    }
    ESP_LOGI(TAG, "Sending command: '%s'", cmd_buf);

    esp_err_t ret = cdc_acm_host_data_tx_blocking(cdc_dev,
                                                  (const uint8_t *)cmd_buf,
                                                  strlen(cmd_buf),
                                                  EXAMPLE_TX_TIMEOUT_MS);
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "Failed to send command '%s': %s", cmd, esp_err_to_name(ret));
        return ret;
    }

    // Clear global response buffer
    xSemaphoreTake(response_mutex, portMAX_DELAY);
    global_response_len = 0;
    memset(global_response_buf, 0, RESPONSE_BUF_SIZE);
    xSemaphoreGive(response_mutex);

    // Wait until a newline is received or timeout occurs
    if (xSemaphoreTake(response_sem, pdMS_TO_TICKS(timeout_ms)) == pdTRUE) {
        xSemaphoreTake(response_mutex, portMAX_DELAY);
        size_t copy_len = (global_response_len < (out_len - 1)) ? global_response_len : (out_len - 1);
        memcpy(out_resp, global_response_buf, copy_len);
        out_resp[copy_len] = '\0';
        xSemaphoreGive(response_mutex);
        ESP_LOGI(TAG, "Received response: %s", out_resp);
        return ESP_OK;
    } else {
        ESP_LOGW(TAG, "Timeout waiting for response to '%s'", cmd);
        return ESP_ERR_TIMEOUT;
    }
}

/*---------------------------------------------------------------
 * TCP Client Management Functions
 *-------------------------------------------------------------*/
/**
 * @brief Adds a new client socket to the global client list.
 */
static void add_client(int sock)
{
    xSemaphoreTake(client_list_mutex, portMAX_DELAY);
    for (int i = 0; i < MAX_CLIENTS; i++) {
        if (client_sockets[i] == -1) {
            client_sockets[i] = sock;
            ESP_LOGI(TAG, "Client added at index %d", i);
            break;
        }
    }
    xSemaphoreGive(client_list_mutex);
}

/**
 * @brief Removes a client socket from the global client list.
 */
static void remove_client(int sock)
{
    xSemaphoreTake(client_list_mutex, portMAX_DELAY);
    for (int i = 0; i < MAX_CLIENTS; i++) {
        if (client_sockets[i] == sock) {
            client_sockets[i] = -1;
            ESP_LOGI(TAG, "Client removed from index %d", i);
            break;
        }
    }
    xSemaphoreGive(client_list_mutex);
}

/**
 * @brief Broadcasts a message to every connected client.
 */
static void broadcast_to_clients(const char *message)
{
    xSemaphoreTake(client_list_mutex, portMAX_DELAY);
    for (int i = 0; i < MAX_CLIENTS; i++) {
        if (client_sockets[i] != -1) {
            send(client_sockets[i], message, strlen(message), 0);
        }
    }
    xSemaphoreGive(client_list_mutex);
}

/*---------------------------------------------------------------
 * TCP Server Tasks
 *-------------------------------------------------------------*/
/**
 * @brief Task that handles a single client connection.
 *
 * The task reads incoming data (commands) from the client.
 * Upon receiving a command, it broadcasts the command to all clients
 * and then sends it to the USB CDC device. The USB device’s response is broadcast as well.
 */
static void handle_client(void *arg)
{
    int client_sock = (int)arg;
    char rx_buffer[TCP_RX_BUFFER_SIZE];

    add_client(client_sock);
    ESP_LOGI(TAG, "Client connected. Starting handler task.");

    while (1) {
        memset(rx_buffer, 0, sizeof(rx_buffer));
        int len = recv(client_sock, rx_buffer, sizeof(rx_buffer) - 1, 0);
        if (len < 0) {
            ESP_LOGE(TAG, "recv failed: errno %d", errno);
            break;
        } else if (len == 0) {
            ESP_LOGI(TAG, "Client disconnected");
            break;
        } else {
            rx_buffer[len] = '\0';
            ESP_LOGI(TAG, "Received from client: %s", rx_buffer);

            // Broadcast the received command to all connected clients
            broadcast_to_clients(rx_buffer);

            // Get the current USB CDC device handle
            xSemaphoreTake(usb_dev_mutex, portMAX_DELAY);
            cdc_acm_dev_hdl_t cdc_dev = g_cdc_dev;
            xSemaphoreGive(usb_dev_mutex);

            if (cdc_dev == NULL) {
                broadcast_to_clients("USB CDC device not connected\r\n");
            } else {
                char resp[RESPONSE_BUF_SIZE] = {0};
                esp_err_t usb_ret = send_command_sync(cdc_dev, rx_buffer, resp, sizeof(resp), 2000);
                if (usb_ret == ESP_OK) {
                    broadcast_to_clients(resp);
                } else {
                    broadcast_to_clients("Timeout or TX error\r\n");
                }
            }
        }
    }

    remove_client(client_sock);
    close(client_sock);
    ESP_LOGI(TAG, "Client handler task ending.");
    vTaskDelete(NULL);
}

/**
 * @brief TCP server task that listens for incoming client connections.
 *
 * For each new connection, a new task is spawned to handle that client.
 */
static void tcp_server_task(void *arg)
{
    struct sockaddr_in server_addr, client_addr;
    socklen_t addr_len = sizeof(client_addr);

    int listen_sock = socket(AF_INET, SOCK_STREAM, IPPROTO_IP);
    if (listen_sock < 0) {
        ESP_LOGE(TAG, "Unable to create socket: errno %d", errno);
        vTaskDelete(NULL);
        return;
    }

    server_addr.sin_family = AF_INET;
    server_addr.sin_addr.s_addr = htonl(INADDR_ANY);
    server_addr.sin_port = htons(TCP_SERVER_PORT);

    if (bind(listen_sock, (struct sockaddr *)&server_addr, sizeof(server_addr)) != 0) {
        ESP_LOGE(TAG, "Socket unable to bind: errno %d", errno);
        close(listen_sock);
        vTaskDelete(NULL);
        return;
    }
    ESP_LOGI(TAG, "Socket bound, port %d", TCP_SERVER_PORT);

    if (listen(listen_sock, MAX_CLIENTS) != 0) {
        ESP_LOGE(TAG, "Error listening: errno %d", errno);
        close(listen_sock);
        vTaskDelete(NULL);
        return;
    }
    ESP_LOGI(TAG, "TCP server listening...");

    while (1) {
        int client_sock = accept(listen_sock, (struct sockaddr *)&client_addr, &addr_len);
        if (client_sock < 0) {
            ESP_LOGE(TAG, "Unable to accept connection: errno %d", errno);
            break;
        }
        ESP_LOGI(TAG, "New client connected");
        xTaskCreate(handle_client, "handle_client_task", 4096, (void *)client_sock, 5, NULL);
    }

    close(listen_sock);
    vTaskDelete(NULL);
}

/*---------------------------------------------------------------
 * Wi-Fi AP Initialization
 *-------------------------------------------------------------*/
/**
 * @brief Initializes Wi-Fi in Access Point (AP) mode.
 */
static void wifi_init_softap(void)
{
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_ap();

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&cfg));

    wifi_config_t ap_config = {
        .ap = {
            .ssid = AP_SSID,
            .ssid_len = strlen(AP_SSID),
            .channel = AP_CHANNEL,
            .password = AP_PASS,
            .max_connection = MAX_STA_CONN,
            .authmode = WIFI_AUTH_WPA_WPA2_PSK,
        },
    };

    if (strlen(AP_PASS) == 0) {
        ap_config.ap.authmode = WIFI_AUTH_OPEN;
    }

    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_AP));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_AP, &ap_config));
    ESP_ERROR_CHECK(esp_wifi_start());

    ESP_LOGI(TAG, "Wi-Fi AP started. SSID: %s, Password: %s", AP_SSID, AP_PASS);
}

/*---------------------------------------------------------------
 * Main Application
 *-------------------------------------------------------------*/
void app_main(void)
{
    // Initialize NVS needed by Wi-Fi
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES ||
        ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ret = nvs_flash_init();
    }
    ESP_ERROR_CHECK(ret);

    // Initialize Wi-Fi in AP mode
    wifi_init_softap();

    // Create mutex for client list
    client_list_mutex = xSemaphoreCreateMutex();

    // Create semaphores and mutexes for USB CDC and responses
    device_disconnected_sem = xSemaphoreCreateBinary();
    usb_dev_mutex           = xSemaphoreCreateMutex();
    response_sem            = xSemaphoreCreateBinary();
    response_mutex          = xSemaphoreCreateMutex();

    // Install USB Host driver
    ESP_LOGI(TAG, "Installing USB Host");
    const usb_host_config_t host_config = {
        .skip_phy_setup = false,
        .intr_flags     = ESP_INTR_FLAG_LEVEL1,
    };
    ESP_ERROR_CHECK(usb_host_install(&host_config));

    // Install CDC-ACM driver
    ESP_LOGI(TAG, "Installing CDC-ACM driver");
    ESP_ERROR_CHECK(cdc_acm_host_install(NULL));

    // Create tasks for USB host event handling and CDC device management
    xTaskCreate(usb_lib_task, "usb_lib_task", 4096, NULL, EXAMPLE_USB_HOST_PRIORITY, NULL);
    xTaskCreate(usb_cdc_task, "usb_cdc_task", 4096, NULL, EXAMPLE_USB_HOST_PRIORITY, NULL);

    // Create the TCP server task
    xTaskCreate(tcp_server_task, "tcp_server_task", 4096, NULL, 5, NULL);

    ESP_LOGI(TAG, "Setup complete. ESP32-S3 is running as an AP with a TCP broadcast server!");
    while (1) {
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}
