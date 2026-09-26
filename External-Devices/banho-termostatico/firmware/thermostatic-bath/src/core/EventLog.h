#pragma once

#include <stddef.h>

// Registro de eventos em RAM, lido por GET /log: o no fica montado sem USB, e o log
// serial e a unica explicacao de uma sequencia que falhou. Guarda as ultimas
// EVENT_LOG_LINES linhas, cada uma com o millis() de quando foi escrita. So o laco
// principal escreve (ISRs e a tarefa do Hub nao chamam estas funcoes).
#if defined(__GNUC__)
#define EVENT_LOG_PRINTF_CHECK __attribute__((format(printf, 1, 2)))
#else
#define EVENT_LOG_PRINTF_CHECK
#endif
void logPrintf(const char* fmt, ...) EVENT_LOG_PRINTF_CHECK;
void logPrintln(const char* line);
// Copia o registro, da linha mais antiga para a mais nova, em `out` (texto, uma linha
// por evento). Devolve o numero de bytes escritos, sem contar o terminador.
size_t eventLogText(char* out, size_t cap);
