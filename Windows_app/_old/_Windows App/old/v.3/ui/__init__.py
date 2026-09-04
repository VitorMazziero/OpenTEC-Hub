from PyQt6.QtCore import QSize

# Configurações globais de UI para todo o projeto
UI_CONFIG = {
    "window_size": QSize(1000, 480),       # Tamanho total da janela
    "block_size": QSize(300, 200),         # Tamanho padrão dos blocos (usado na página de Parâmetros)
    "margin": 10,
    "spacing": 10,
    "group_spacing": 10,
    "exp_values": {
         "label_width": 80,
         "edit_width": 50,
         "row_height": 30,
         "spacing": 10,
         "square_widget_size": QSize(300, 600),
    }
}
