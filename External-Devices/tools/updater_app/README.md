# Atualizador de dispositivos externos

Aplicativo PySide6 que centraliza a compilação e a gravação OTA dos cinco dispositivos externos. Faz pela interface o que `tools/Publish-OtaFirmware.ps1` faz por linha de comando, para um dispositivo de cada vez.

```powershell
python -m pip install -r requirements.txt
python app.py
```

## O que a janela mostra

Uma linha por dispositivo, com o estado necessário para decidir o que gravar:

| Coluna | Origem |
|---|---|
| Estado | `online`, `registered` e a idade do último contato, calculada de `hub_time_ms` menos `last_hello_ms`/`last_data_ms` |
| Endereço | `ip` no diretório do Hub; na falta dele, o AP padrão do dispositivo; acima de ambos, um endereço manual |
| Versão no dispositivo | campo `version` que o nó publicou no `/nodeHello` |
| Versão no repositório | constante declarada no fonte do firmware, ou seja, o que uma compilação nova instalaria |
| Imagem a enviar | `.bin` mais recente entre `tools/.build/<dispositivo>`, `build/esp32.esp32.esp32*` e a raiz do sketch |

As duas colunas de versão ficam verdes quando coincidem e âmbar quando divergem. A divergência é sinalizada, nunca corrigida sozinha: quem decide o que é gravado durante um ensaio é o operador.

## Operação

1. **Atualizar estado** consulta `http://<hub>/nodes` e preenche a tabela. Sem o Hub, cada dispositivo recai no seu AP padrão e a atualização é feita conectando-se ao Wi-Fi dele.
2. Marque os dispositivos na primeira coluna.
3. **Compilar**, **Enviar OTA** ou **Compilar e enviar**. O envio pede confirmação listando destino e imagem de cada dispositivo.
4. Após o reinício, **Atualizar estado** de novo confirma a versão que cada nó passou a reportar.

O botão direito sobre uma linha permite escolher um `.bin` manualmente, definir o endereço à mão e testar a rota `/update` antes de gravar.

Os dispositivos são gravados um de cada vez. Eles compartilham o ponto de acesso do Hub, e envios simultâneos por esse enlace são mais lentos e menos confiáveis do que os mesmos envios em sequência; uma falha também deixa os demais intactos e ainda graváveis.

## Contrato com o firmware

As cinco imagens são enviadas como `POST /update` com uma parte `multipart/form-data` chamada `firmware`, com `Content-Length` declarado. Os firmwares recusam nomes contendo `merged`, `bootloader` ou `partitions`; o aplicativo aplica a mesma regra antes de enviar, para que a recusa aconteça aqui e não no meio da gravação da flash.

Endereços de AP padrão, FQBN e caminhos de sketch são os mesmos de `Publish-OtaFirmware.ps1` e `tests/test_catalog.py` compara as duas tabelas campo a campo — se divergirem, o teste falha.

## Verificação

```powershell
python tests/run_all.py
```

Quatro conjuntos, todos sem hardware: catálogo e paridade com o publicador PowerShell, leitura de `/nodes` (incluindo `0.0.0.0` e a sentinela `999999`), envio OTA contra um servidor local que confere o corpo recebido byte a byte, e a montagem da janela em modo offscreen. Testes estáticos não substituem validação de bancada.

## Estrutura

| Arquivo | Responsabilidade |
|---|---|
| `app.py` | ponto de entrada |
| `updater/devices.py` | catálogo dos cinco dispositivos e caminhos do repositório |
| `updater/hub.py` | leitura de `/nodes` e teste da rota `/update` |
| `updater/firmware.py` | descoberta de imagens e leitura da versão no fonte |
| `updater/compiler.py` | chamada ao `arduino-cli` |
| `updater/ota.py` | upload multipart com progresso e cancelamento |
| `updater/jobs.py` | fila em segundo plano |
| `updater/mainwindow.py` | interface |

A compilação usa as bibliotecas compartilhadas configuráveis no campo *Bibliotecas Arduino*, por padrão `C:\Users\vitor\OneDrive\Documentos\Arduino\libraries`, e grava em `tools/.build/<dispositivo>`, que não é versionado.
