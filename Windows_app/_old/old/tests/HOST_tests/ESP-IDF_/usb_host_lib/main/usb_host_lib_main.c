/******************************************************************************
 * Integrated ESP32-S3 Firmware Example (with Actual USB Write/Read)
 *
 * Features:
 *   - USB Host (using Espressif USB Host library and external class_driver_task)
 *     → Connects to a 5V USB device (e.g. an FTDI FT232R USB UART)
 *
 *   - WiFi SoftAP "ModuloTECNAL" (password "ModuloTECNAL")
 *     → A TCP server listens on port 23 (telnet-like)
 *
 *   - JSON command parsing (using cJSON)
 *     → Commands (e.g. {"Motorset":200}) are parsed and forwarded to the USB device.
 *
 *   - Periodic sensor polling:
 *     → Every second, commands (e.g. "b", "d", "g") are sent to the USB device.
 *        Responses are logged and forwarded via TCP and via ESP‑NOW.
 *
 *   - ESP‑NOW Configuration:
 *     → ESP‑NOW is initialized and configured to send/receive messages with other ESP32 boards.
 *
 * Note: Make sure your USB enumeration (for example, within your class driver) obtains
 *       and stores the device’s USB pipe handles (for writing and reading) in the global
 *       variables shown below. Adjust endpoints and transfer settings as required by your USB device.
 ******************************************************************************/

#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <assert.h>
#include <errno.h>

/* FreeRTOS & ESP‑IDF includes */
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/queue.h"
#include "freertos/event_groups.h"
#include "esp_log.h"
#include "esp_intr_alloc.h"
#include "usb/usb_host.h"
#include "driver/gpio.h"
#include "nvs_flash.h"
#include "esp_event.h"
#include "esp_wifi.h"
#include "esp_netif.h"

/* LWIP (TCP Sockets) */
#include "lwip/sockets.h"
#include "lwip/netdb.h"

/* ESP‑NOW include */
#include "esp_now.h"

/* cJSON include */
#include "cJSON.h"

/*-------------------------- Definitions --------------------------*/

/* WiFi settings */
#define WIFI_SSID           "ModuloTECNAL"
#define WIFI_PASSWORD       "ModuloTECNAL"
#define WIFI_AP_MAX_CONN    4

/* TCP server settings */
#define TCP_PORT            23
#define TCP_RX_BUF_SIZE     128

/* USB Host settings & task priorities */
#define HOST_LIB_TASK_PRIORITY    2
#define CLASS_TASK_PRIORITY       3
#define APP_QUIT_PIN              CONFIG_APP_QUIT_PIN  // configure in menuconfig

/* Tag for ESP_LOG */
static const char *TAG = "APP";

/*-------------------------- External USB Class Driver --------------------------*/
/* These functions should be provided by your USB class driver code. */
extern void class_driver_task(void *arg);
extern void class_driver_client_deregister(void);

/*-------------------------- Global Variables for TCP --------------------------*/
static int tcp_client_sock = -1;
static int tcp_server_sock = -1;

/*-------------------------- Global USB Device & Pipe Handles --------------------------*/
/* These globals must be set by your USB enumeration / class driver code. For example,
   once the device is enumerated, you might store its handle and the pipe handles for the
   bulk‑out (write) and bulk‑in (read) endpoints. */
static usb_device_handle_t s_usb_dev_handle = NULL;
static usb_pipe_handle_t s_usb_pipe_out = NULL; // for writing to the device
static usb_pipe_handle_t s_usb_pipe_in  = NULL; // for reading from the device

/*-------------------------- USB Synchronous Transfer Helper --------------------------*/
/*
   This helper implements a synchronous transfer using the USB Host API. It allocates
   a transfer, submits it, waits (using a semaphore) for the callback to be invoked, and
   then frees the transfer.
*/

typedef struct {
    SemaphoreHandle_t sem;
    esp_err_t result;
    int actual_length;
} usb_sync_context_t;

static void usb_transfer_callback(usb_host_transfer_t *transfer)
{
    usb_sync_context_t *ctx = (usb_sync_context_t *)transfer->context;
    if (ctx) {
        ctx->result = transfer->result;
        ctx->actual_length = transfer->actual_length;
        xSemaphoreGive(ctx->sem);
    }
}

static esp_err_t usb_sync_transfer(usb_pipe_handle_t pipe, uint8_t *buffer, size_t length,
                                   uint32_t timeout_ms, int *transferred)
{
    usb_sync_context_t sync_ctx = {0};
    sync_ctx.sem = xSemaphoreCreateBinary();
    if (sync_ctx.sem == NULL) {
        return ESP_ERR_NO_MEM;
    }

    usb_host_transfer_t *transfer = NULL;
    esp_err_t ret = usb_host_transfer_alloc(timeout_ms, &transfer);
    if (ret != ESP_OK) {
        vSemaphoreDelete(sync_ctx.sem);
        return ret;
    }

    transfer->data_buffer = buffer;
    transfer->data_length = length;
    transfer->callback = usb_transfer_callback;
    transfer->context = &sync_ctx;
    transfer->endpoint_handle = pipe;

    ret = usb_host_transfer_submit(transfer, timeout_ms);
    if (ret != ESP_OK) {
        usb_host_transfer_free(transfer);
        vSemaphoreDelete(sync_ctx.sem);
        return ret;
    }

    if (xSemaphoreTake(sync_ctx.sem, pdMS_TO_TICKS(timeout_ms)) != pdTRUE) {
        usb_host_transfer_cancel(transfer);
        usb_host_transfer_free(transfer);
        vSemaphoreDelete(sync_ctx.sem);
        return ESP_ERR_TIMEOUT;
    }

    ret = sync_ctx.result;
    if (transferred) {
        *transferred = sync_ctx.actual_length;
    }
    usb_host_transfer_free(transfer);
    vSemaphoreDelete(sync_ctx.sem);
    return ret;
}

/*-------------------------- USB Write/Read Command Function --------------------------*/
/*
   Sends a command string to the USB device (using the bulk‑out pipe) and, if requested,
   waits for and reads the response from the device (using the bulk‑in pipe). It is assumed
   that the device uses a simple protocol (for example, command strings terminated by a newline)
   and that the response will be a null‑terminated string.
*/
static int usb_send_command(const char *cmd, bool wait_response, char *response, size_t response_len)
{
    if (s_usb_pipe_out == NULL) {
        ESP_LOGE(TAG, "USB output pipe not available!");
        return -1;
    }

    /* Write the command to the device.
       You may want to append a newline or any termination characters required by your device.
       Here we assume the command string (cmd) is already properly formatted. */
    size_t cmd_len = strlen(cmd);
    esp_err_t ret = usb_sync_transfer(s_usb_pipe_out, (uint8_t *)cmd, cmd_len, 2000, NULL);
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "USB write failed: 0x%x", ret);
        return ret;
    }

    /* If a response is not required, return here. */
    if (!wait_response) {
        return 0;
    }

    if (s_usb_pipe_in == NULL) {
        ESP_LOGE(TAG, "USB input pipe not available!");
        return -1;
    }

    /* Clear the response buffer before reading. */
    memset(response, 0, response_len);
    int bytes_read = 0;
    ret = usb_sync_transfer(s_usb_pipe_in, (uint8_t *)response, response_len - 1, 2000, &bytes_read);
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "USB read failed: 0x%x", ret);
        return ret;
    }

    /* Ensure the response is null-terminated. */
    response[bytes_read] = '\0';
    ESP_LOGI(TAG, "USB command response: %s", response);
    return 0;
}

/*-------------------------- JSON Command Handling --------------------------*/
/*
   Parses a JSON command and, if a "Motorset" key is found, sends corresponding
   commands to the USB device. After each command is sent, waits for the USB device’s
   response and then prints it to the serial output and forwards it to any connected TCP client.
*/
static void handle_json_command(const char *json_str)
{
    ESP_LOGI(TAG, "Parsing JSON: %s", json_str);
    cJSON *json = cJSON_Parse(json_str);
    if (!json) {
        ESP_LOGE(TAG, "Invalid JSON");
        return;
    }
    cJSON *motor_item = cJSON_GetObjectItem(json, "Motorset");
    if (motor_item && cJSON_IsNumber(motor_item)) {
        int rpm = motor_item->valueint;
        char cmd[32];
        char response[64];  // Buffer for USB response
        if (rpm == 0) {
            snprintf(cmd, sizeof(cmd), "0V");
            usb_send_command(cmd, true, response, sizeof(response));
            ESP_LOGI(TAG, "Command response: %s", response);
            if (tcp_client_sock != -1) {
                send(tcp_client_sock, response, strlen(response), 0);
                send(tcp_client_sock, "\n", 1, 0);
            }
            snprintf(cmd, sizeof(cmd), "0A");
            usb_send_command(cmd, true, response, sizeof(response));
            ESP_LOGI(TAG, "Command response: %s", response);
            if (tcp_client_sock != -1) {
                send(tcp_client_sock, response, strlen(response), 0);
                send(tcp_client_sock, "\n", 1, 0);
            }
        } else {
            snprintf(cmd, sizeof(cmd), "1V");
            usb_send_command(cmd, true, response, sizeof(response));
            ESP_LOGI(TAG, "Command response: %s", response);
            if (tcp_client_sock != -1) {
                send(tcp_client_sock, response, strlen(response), 0);
                send(tcp_client_sock, "\n", 1, 0);
            }
            snprintf(cmd, sizeof(cmd), "%dA", rpm);
            usb_send_command(cmd, true, response, sizeof(response));
            ESP_LOGI(TAG, "Command response: %s", response);
            if (tcp_client_sock != -1) {
                send(tcp_client_sock, response, strlen(response), 0);
                send(tcp_client_sock, "\n", 1, 0);
            }
        }
    }
    cJSON_Delete(json);
}

/*-------------------------- TCP Server Task --------------------------*/
static void tcp_server_task(void *pvParameters)
{
    char rx_buffer[TCP_RX_BUF_SIZE];
    struct sockaddr_in server_addr, client_addr;
    socklen_t addr_len = sizeof(client_addr);

    tcp_server_sock = socket(AF_INET, SOCK_STREAM, IPPROTO_IP);
    if (tcp_server_sock < 0) {
        ESP_LOGE(TAG, "Unable to create socket: errno %d", errno);
        vTaskDelete(NULL);
        return;
    }
    server_addr.sin_family = AF_INET;
    server_addr.sin_addr.s_addr = htonl(INADDR_ANY);
    server_addr.sin_port = htons(TCP_PORT);

    if (bind(tcp_server_sock, (struct sockaddr *)&server_addr, sizeof(server_addr)) != 0) {
        ESP_LOGE(TAG, "Socket unable to bind: errno %d", errno);
        close(tcp_server_sock);
        vTaskDelete(NULL);
        return;
    }
    if (listen(tcp_server_sock, 1) != 0) {
        ESP_LOGE(TAG, "Error during listen: errno %d", errno);
        close(tcp_server_sock);
        vTaskDelete(NULL);
        return;
    }
    ESP_LOGI(TAG, "TCP server listening on port %d", TCP_PORT);

    while (1) {
        tcp_client_sock = accept(tcp_server_sock, (struct sockaddr *)&client_addr, &addr_len);
        if (tcp_client_sock < 0) {
            ESP_LOGE(TAG, "Unable to accept connection: errno %d", errno);
            break;
        }
        ESP_LOGI(TAG, "TCP client connected");
        while (1) {
            int len = recv(tcp_client_sock, rx_buffer, sizeof(rx_buffer) - 1, 0);
            if (len < 0) {
                ESP_LOGE(TAG, "recv failed: errno %d", errno);
                break;
            } else if (len == 0) {
                ESP_LOGI(TAG, "TCP client disconnected");
                break;
            } else {
                rx_buffer[len] = '\0';
                ESP_LOGI(TAG, "Received via TCP: %s", rx_buffer);
                handle_json_command(rx_buffer);
            }
        }
        close(tcp_client_sock);
        tcp_client_sock = -1;
    }
    close(tcp_server_sock);
    tcp_server_sock = -1;
    vTaskDelete(NULL);
}

/*-------------------------- ESP‑NOW Callbacks and Peer Setup --------------------------*/
static void esp_now_recv_cb(const esp_now_recv_info_t *recv_info, const uint8_t *data, int len)
{
    char mac_str[18];
    if (recv_info && recv_info->src_addr) {
        snprintf(mac_str, sizeof(mac_str), "%02x:%02x:%02x:%02x:%02x:%02x",
                 recv_info->src_addr[0], recv_info->src_addr[1],
                 recv_info->src_addr[2], recv_info->src_addr[3],
                 recv_info->src_addr[4], recv_info->src_addr[5]);
    } else {
        strcpy(mac_str, "Unknown");
    }
    ESP_LOGI(TAG, "ESP‑NOW received from %s: %.*s", mac_str, len, data);
}

static void esp_now_send_cb(const uint8_t *mac_addr, esp_now_send_status_t status)
{
    char mac_str[18];
    if (mac_addr) {
        snprintf(mac_str, sizeof(mac_str), "%02x:%02x:%02x:%02x:%02x:%02x",
                 mac_addr[0], mac_addr[1], mac_addr[2],
                 mac_addr[3], mac_addr[4], mac_addr[5]);
    } else {
        strcpy(mac_str, "Broadcast");
    }
    ESP_LOGI(TAG, "ESP‑NOW sent to %s, status: %d", mac_str, status);
}

/* Adds the broadcast peer for ESP‑NOW */
static void add_broadcast_peer(void)
{
    esp_now_peer_info_t peerInfo = {0};
    /* For broadcast, set peer address to FF:FF:FF:FF:FF:FF */
    memset(peerInfo.peer_addr, 0xFF, 6);
    peerInfo.channel = 0;   // Use current WiFi channel
    peerInfo.ifidx = WIFI_IF_STA;
    peerInfo.encrypt = false;
    if (esp_now_add_peer(&peerInfo) != ESP_OK) {
        ESP_LOGE(TAG, "Failed to add broadcast peer");
    }
}

/*-------------------------- Sensor Data Task --------------------------*/
static void sensor_data_task(void *pvParameters)
{
    char response[64];
    char json_buf[128];
    float temperature = -1.0, ph = -1.0, oxygen = -1.0;
    while (1) {
        if (usb_send_command("b", true, response, sizeof(response)) == 0) {
            temperature = atof(response);
        }
        if (usb_send_command("d", true, response, sizeof(response)) == 0) {
            ph = atof(response);
        }
        if (usb_send_command("g", true, response, sizeof(response)) == 0) {
            oxygen = atof(response);
        }
        float time_sec = (float)(xTaskGetTickCount() / configTICK_RATE_HZ);
        snprintf(json_buf, sizeof(json_buf),
                 "{\"Time\":%.1f,\"Tempval\":%.2f,\"pHval\":%.2f,\"Oxyval\":%.1f}",
                 time_sec, temperature, ph, oxygen);
        ESP_LOGI(TAG, "Sensor JSON: %s", json_buf);
        /* If a TCP client is connected, send the JSON string */
        if (tcp_client_sock != -1) {
            send(tcp_client_sock, json_buf, strlen(json_buf), 0);
            send(tcp_client_sock, "\n", 1, 0);
        }
        /* Broadcast the JSON message via ESP‑NOW */
        esp_err_t send_rc = esp_now_send(NULL, (uint8_t *)json_buf, strlen(json_buf));
        if (send_rc != ESP_OK) {
            ESP_LOGE(TAG, "ESP‑NOW send error: %d", send_rc);
        }
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

/*-------------------------- USB Host Library Task --------------------------*/
#ifdef CONFIG_USB_HOST_ENABLE_ENUM_FILTER_CALLBACK
static bool set_config_cb(const usb_device_desc_t *dev_desc, uint8_t *bConfigurationValue)
{
    if (dev_desc->bNumConfigurations > 1) {
        *bConfigurationValue = 2;
    } else {
        *bConfigurationValue = 1;
    }
    return true;
}
#endif

static void usb_host_lib_task(void *arg)
{
    ESP_LOGI(TAG, "Installing USB Host Library");
    usb_host_config_t host_config = {
        .skip_phy_setup = false,
        .intr_flags = ESP_INTR_FLAG_LEVEL1,
#ifdef CONFIG_USB_HOST_ENABLE_ENUM_FILTER_CALLBACK
        .enum_filter_cb = set_config_cb,
#endif
    };
    ESP_ERROR_CHECK(usb_host_install(&host_config));

    /* Notify that the USB host library is installed */
    xTaskNotifyGive((TaskHandle_t)arg);

    bool has_clients = true;
    bool has_devices = false;
    while (has_clients) {
        uint32_t event_flags;
        ESP_ERROR_CHECK(usb_host_lib_handle_events(portMAX_DELAY, &event_flags));
        if (event_flags & USB_HOST_LIB_EVENT_FLAGS_NO_CLIENTS) {
            ESP_LOGI(TAG, "USB host: no more clients");
            if (ESP_OK == usb_host_device_free_all()) {
                ESP_LOGI(TAG, "All devices freed");
                has_clients = false;
            } else {
                has_devices = true;
            }
        }
        if (has_devices && (event_flags & USB_HOST_LIB_EVENT_FLAGS_ALL_FREE)) {
            ESP_LOGI(TAG, "USB host: all devices free");
            has_clients = false;
        }
    }
    ESP_LOGI(TAG, "Uninstalling USB Host Library");
    ESP_ERROR_CHECK(usb_host_uninstall());
    vTaskSuspend(NULL);
}

/*-------------------------- Application Quit ISR --------------------------*/
static void app_quit_isr_handler(void *arg)
{
    typedef enum {
        APP_EVENT_QUIT = 0,
    } app_event_group_t;
    typedef struct {
        app_event_group_t event;
    } app_event_msg_t;

    app_event_msg_t evt = { .event = 0 };
    BaseType_t xHigherPriorityTaskWoken = pdFALSE;
    if (app_event_queue) {
        xQueueSendFromISR(app_event_queue, &evt, &xHigherPriorityTaskWoken);
    }
    if (xHigherPriorityTaskWoken) {
        portYIELD_FROM_ISR();
    }
}

/*-------------------------- Application Event Queue --------------------------*/
QueueHandle_t app_event_queue = NULL;

/*-------------------------- Main Application (app_main) --------------------------*/
void app_main(void)
{
    ESP_LOGI(TAG, "Starting integrated USB host + WiFi + ESP‑NOW + JSON app");

    /* Initialize NVS */
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ESP_ERROR_CHECK(nvs_flash_init());
    }

    /* Initialize WiFi in AP+STA mode so that both the SoftAP and ESP‑NOW work */
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_ap();
    esp_netif_create_default_wifi_sta();
    wifi_init_config_t wifi_cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&wifi_cfg));
    wifi_config_t ap_config = {
        .ap = {
            .ssid = WIFI_SSID,
            .ssid_len = strlen(WIFI_SSID),
            .password = WIFI_PASSWORD,
            .max_connection = WIFI_AP_MAX_CONN,
            .authmode = strlen(WIFI_PASSWORD) ? WIFI_AUTH_WPA2_PSK : WIFI_AUTH_OPEN,
        },
    };
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_APSTA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_AP, &ap_config));
    ESP_ERROR_CHECK(esp_wifi_start());
    ESP_LOGI(TAG, "WiFi SoftAP started. SSID: %s Password: %s", WIFI_SSID, WIFI_PASSWORD);

    /* Initialize ESP‑NOW */
    ESP_ERROR_CHECK(esp_now_init());
    ESP_ERROR_CHECK(esp_now_register_recv_cb(esp_now_recv_cb));
    ESP_ERROR_CHECK(esp_now_register_send_cb(esp_now_send_cb));
    add_broadcast_peer();

    /* Create TCP server task */
    xTaskCreate(tcp_server_task, "tcp_server_task", 4096, NULL, 5, NULL);

    /* Create sensor data polling task */
    xTaskCreate(sensor_data_task, "sensor_data_task", 4096, NULL, 5, NULL);

    /* Create USB host library task.
       Use the current task handle to notify when the USB host library is installed. */
    TaskHandle_t host_lib_task_hdl;
    BaseType_t task_created = xTaskCreatePinnedToCore(usb_host_lib_task,
                                                      "usb_host_lib_task",
                                                      4096,
                                                      xTaskGetCurrentTaskHandle(),
                                                      HOST_LIB_TASK_PRIORITY,
                                                      &host_lib_task_hdl,
                                                      0);
    assert(task_created == pdTRUE);
    /* Wait until the USB host library is installed */
    ulTaskNotifyTake(pdFALSE, pdMS_TO_TICKS(1000));

    /* Create the external USB class driver task.
       (Provided externally by your USB class driver code.)
       This task should enumerate the USB device and store the device handle as well as
       the pipe handles (s_usb_pipe_out and s_usb_pipe_in) for further USB transfers.
    */
    TaskHandle_t class_driver_task_hdl;
    task_created = xTaskCreatePinnedToCore(class_driver_task,
                                           "class_driver_task",
                                           5 * 1024,
                                           NULL,
                                           CLASS_TASK_PRIORITY,
                                           &class_driver_task_hdl,
                                           0);
    assert(task_created == pdTRUE);

    /* Initialize the quit-button (BOOT button) to allow user to stop the USB host */
    const gpio_config_t input_pin = {
        .pin_bit_mask = BIT64(APP_QUIT_PIN),
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = GPIO_PULLUP_ENABLE,
        .intr_type = GPIO_INTR_NEGEDGE,
    };
    ESP_ERROR_CHECK(gpio_config(&input_pin));
    ESP_ERROR_CHECK(gpio_install_isr_service(ESP_INTR_FLAG_LEVEL1));
    ESP_ERROR_CHECK(gpio_isr_handler_add(APP_QUIT_PIN, app_quit_isr_handler, NULL));

    /* Create the application event queue */
    app_event_queue = xQueueCreate(10, sizeof(uint32_t));
    uint32_t evt;
    while (1) {
        if (xQueueReceive(app_event_queue, &evt, portMAX_DELAY)) {
            if (evt == 0) {
                ESP_LOGI(TAG, "Quit button pressed. Shutting down USB host...");
                usb_host_lib_info_t lib_info;
                usb_host_lib_info(&lib_info);
                if (lib_info.num_devices != 0) {
                    ESP_LOGW(TAG, "Devices are still attached!");
                }
                break;
            }
        }
    }

    /* Signal USB host shutdown */
    class_driver_client_deregister();
    vTaskDelay(pdMS_TO_TICKS(10));
    vTaskDelete(class_driver_task_hdl);
    vTaskDelete(host_lib_task_hdl);
    gpio_isr_handler_remove(APP_QUIT_PIN);
    vQueueDelete(app_event_queue);
    ESP_LOGI(TAG, "Application shutdown complete.");
}
