idf_component_register(SRCS "usb_cdc_example_main.c"
                    INCLUDE_DIRS "."
                    PRIV_REQUIRES usb nvs_flash esp_wifi esp_event esp_netif esp_timer mqtt
                    )

