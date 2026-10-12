// Simulacao no PC da bomba 3.13: compila os cabecalhos REAIS do firmware que nao dependem de
// Arduino (JsonScan.h, DoseMath.h, FrameValidate.h) e percorre os casos da integracao com o
// Hub. Nao prova motor, temporizacao real, Wi-Fi nem NVS.

#include <cstdio>
#include <cstring>
#include <string>

#include "protocol/JsonScan.h"
#include "control/DoseMath.h"
#include "control/FrameValidate.h"

static int g_failures = 0;
static int g_checks = 0;

#define CHECK(cond, msg) do { g_checks++; if (!(cond)) { g_failures++; std::printf("  FALHOU: %s (%s:%d)\n", msg, __FILE__, __LINE__); } } while (0)

static bool same(const char* a, const char* b) {
    if (!a || !b) return a == b;
    return std::strcmp(a, b) == 0;
}

static FrameContext idleCtx() {
    FrameContext c{};
    c.profileBusy = false;
    c.doseActive = false;
    c.dose = {0.35f, 300.0f, 120.0f, 5.4f, 50.0f, 0.0f};
    return c;
}

static void cmdIds() {
    std::printf("== A. cmd_id do Hub lido como inteiro exato\n");
    uint32_t id = 0;
    CHECK(parseJsonUint32("{\"cmd_id\":999999001,\"mode\":0}", "cmd_id", id) && id == 999999001u, "999999001 exato");
    CHECK(parseJsonUint32("{\"cmd_id\":123456002}", "cmd_id", id) && id == 123456002u, "vizinhos nao colidem");
    CHECK(parseJsonUint32("{\"cmd_id\":4294967295}", "cmd_id", id) && id == 4294967295u, "maximo de 32 bits");
    CHECK(!parseJsonUint32("{\"cmd_id\":4294967296}", "cmd_id", id), "estouro recusado");
    CHECK(!parseJsonUint32("{\"cmd_id\":-5}", "cmd_id", id), "negativo recusado");
    CHECK(!parseJsonUint32("{\"cmd_id\":1.5}", "cmd_id", id), "fracao recusada");
    CHECK(!parseJsonUint32("{\"cmd_id\":\"7\"}", "cmd_id", id), "string recusada");
    CHECK(!parseJsonUint32("{\"ack_cmd_id\":7}", "cmd_id", id), "outra chave nao casa");
    CHECK(!jsonHasKey("{\"speed_ms\":100}", "speed"), "speed nao casa com speed_ms");
    CHECK(!jsonHasKey("{\"dose_speed\":120}", "speed"), "speed nao casa com dose_speed");
}

static void doseRules() {
    std::printf("== B. regras da dosagem (DoseMath.h)\n");
    DoseConfig ok{0.35f, 300.0f, 120.0f, 5.4f, 50.0f, 0.0f};
    CHECK(doseValidateCode(ok) == nullptr, "caso do cultivo valido");
    DoseConfig c = ok;
    c.volumeMl = 0.0f;   CHECK(same(doseValidateCode(c), "dose_ml"), "dose_ml zero");
    c = ok; c.periodS = 1.0f; CHECK(same(doseValidateCode(c), "dose_period"), "periodo < 2 s");
    c = ok; c.speed = 0.5f;   CHECK(same(doseValidateCode(c), "dose_speed"), "velocidade < 1");
    c = ok; c.rateMlMin = 0;  CHECK(same(doseValidateCode(c), "dose_rate"), "sem vazao medida");
    c = ok; c.totalMl = -1;   CHECK(same(doseValidateCode(c), "dose_total"), "total negativo");
    c = ok; c.deadMs = 2500;  CHECK(same(doseValidateCode(c), "dose_dead"), "atraso > 2 s");
    // 200 ms de entrega a 5,4 mL/min = 0,018 mL (0,0181 por folga de float); abaixo nao passa.
    c = ok; c.volumeMl = 0.0181f; c.periodS = 16.0f;
    CHECK(doseValidateCode(c) == nullptr, "pulso de 200 ms aceito");
    c.volumeMl = 0.015f;
    CHECK(same(doseValidateCode(c), "dose_short"), "pulso < 200 ms recusado");
    // Folga de 0,5 s: pulso de 3,9 s nao cabe em 4 s, cabe em 4,5 s.
    c = ok; c.periodS = 4.0f;
    CHECK(same(doseValidateCode(c), "dose_fit"), "pulso sem folga recusado");
    c.periodS = 4.5f;
    CHECK(doseValidateCode(c) == nullptr, "pulso com 0,5 s de folga aceito");

    // Compensacao do atraso de partida.
    const float onMs = dosePulseMs(0.09f, 5.4f, 40.0f);
    CHECK(onMs > 1039.0f && onMs < 1041.0f, "pulso de 0,09 mL com t0 40 ms = 1040 ms");
    const float ml = doseCountedMl(5.4f, 1040.0f, 40.0f);
    CHECK(ml > 0.0899f && ml < 0.0901f, "volume contado desconta t0");
    CHECK(doseCountedMl(5.4f, 30.0f, 40.0f) == 0.0f, "motor menor que t0 nao conta volume");
    CHECK(doseCountedMl(5.4f, 1000.0f, 0.0f) == doseCountedMl(5.4f, 1000.0f, 0.0f), "t0 = 0 como 3.12");
}

static void frames() {
    std::printf("== C. validacao do quadro inteiro (FrameValidate.h)\n");
    FrameContext idle = idleCtx();
    CHECK(validateFrame("{\"cmd_id\":1,\"mode\":1,\"init_t\":0,\"final_t\":60,\"lambda_const\":1}", idle) == nullptr, "perfil valido");
    CHECK(same(validateFrame("{\"mode\":9}", idle), "mode_range"), "modo fora da faixa");
    CHECK(same(validateFrame("{\"mode\":5,\"num_segments\":1}", idle), "seg_range"), "segmentos < 2");
    CHECK(validateFrame("{\"command\":\"reset_volume\"}", idle) == nullptr, "zerar volume");
    CHECK(validateFrame("{\"pid_kp\":0.5,\"pid_ki\":0.05,\"pid_kd\":0.001}", idle) == nullptr, "PID");

    // Calibracao: atomica, forma C0+C1, so ociosa.
    const char* calOk = "{\"a1\":0,\"b1\":0,\"k1\":0,\"f1\":0.028,\"c1\":1.76,\"k2\":0,\"f2\":0.028,\"c2\":1.76,\"transition_speed\":500}";
    CHECK(validateFrame(calOk, idle) == nullptr, "curva linear continua aceita");
    CHECK(same(validateFrame("{\"a1\":0,\"b1\":0,\"k1\":0,\"f1\":0.028}", idle), "cal_incomplete"), "curva incompleta");
    CHECK(same(validateFrame("{\"a1\":0,\"b1\":0,\"k1\":0,\"f1\":0.028,\"c1\":1.76,\"k2\":0,\"f2\":0.028,\"c2\":1.76,\"transition_speed\":1000}", idle), "cal_st_range"), "St fora de (0,1000)");
    CHECK(same(validateFrame("{\"a1\":0,\"b1\":0,\"k1\":0,\"f1\":0.028,\"c1\":1.76,\"k2\":0,\"f2\":0.050,\"c2\":1.76,\"transition_speed\":500}", idle), "cal_shape"), "salto de derivada em St");
    FrameContext busy = idle; busy.profileBusy = true;
    CHECK(same(validateFrame(calOk, busy), "cal_busy"), "curva com perfil rodando");

    // Quadro misto: perfil valido + curva invalida -> recusa o quadro inteiro.
    CHECK(same(validateFrame("{\"mode\":1,\"lambda_const\":1,\"a1\":0,\"f1\":0.028}", idle), "cal_incomplete"), "quadro misto recusado inteiro");

    // Dosagem.
    CHECK(validateFrame("{\"dose_ml\":0.35,\"dose_period_s\":300,\"dose_speed\":120,\"dose_rate_mlmin\":5.4,\"dose_total_ml\":50,\"dose\":1}", idle) == nullptr, "iniciar dosagem");
    CHECK(same(validateFrame("{\"dose_ml\":0.35,\"dose_period_s\":3,\"dose\":1}", idle), "dose_fit"), "periodo menor que o pulso");
    CHECK(same(validateFrame("{\"dose\":1}", busy), "dose_busy"), "dosagem com perfil rodando");
    CHECK(validateFrame("{\"mode\":0,\"dose\":1}", busy) == nullptr, "mode 0 no mesmo quadro libera a dosagem");
    CHECK(validateFrame("{\"command\":\"stop\",\"dose\":1}", busy) == nullptr, "stop no mesmo quadro libera a dosagem");
    CHECK(same(validateFrame("{\"dose\":2}", idle), "dose_range"), "dose diferente de 0/1");
    CHECK(same(validateFrame("{\"dose_dead_ms\":-1}", idle), "dose_dead"), "atraso negativo");
    // Parametros soltos com a dosagem parada so sao guardados: nao precisam fechar.
    CHECK(validateFrame("{\"dose_period_s\":3}", idle) == nullptr, "parametro solto com dosagem parada");
    FrameContext dosing = idle; dosing.doseActive = true;
    CHECK(same(validateFrame("{\"dose_period_s\":3}", dosing), "dose_fit"), "parametro invalido com dosagem ativa");
    CHECK(validateFrame("{\"dose_period_s\":3,\"dose\":0}", dosing) == nullptr, "parar com parametro solto");
    CHECK(validateFrame("{\"mode\":0,\"dose_period_s\":3}", dosing) == nullptr, "mode 0 cancela antes de validar");
    CHECK(validateFrame("{\"command\":\"save_config\",\"dose_period_s\":300}", dosing) == nullptr, "save_config nao conta como cancelamento");
    CHECK(same(validateFrame("{\"command\":\"save_config\",\"dose_period_s\":3}", dosing), "dose_fit"), "save_config nao pula a validacao");
}

int main() {
    cmdIds();
    doseRules();
    frames();
    std::printf("%d verificacoes, %d falha(s)\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
