/*
 * abi.c — проверка границы НАСТОЯЩИМ компилятором C.
 *
 * Тест на Rust проверил бы только то, что Rust согласен сам с собой:
 * раскладку структур он вывел бы из собственного объявления, а заголовок мог
 * бы при этом врать. Здесь всё наоборот — структуры берутся из
 * include/method_core.h, и если хоть одно поле переехало, тест сломается ровно
 * так же, как сломался бы Swift.
 *
 * Сценарий один и сквозной:
 *   1. создать движок;
 *   2. объявить три маршрута на РАЗНЫХ осях обхода и одну полосу;
 *   3. подать серию проб, пока измерения не станут достоверными;
 *   4. получить решение и подтвердить применение;
 *   5. убить активный маршрут и убедиться, что движок ушёл на ДРУГУЮ ось;
 *   6. спросить оценку; выгрузить и загрузить состояние;
 *   7. всё освободить.
 *
 * Заодно проверяются отказы: NULL, чужой дескриптор, повтор идентификатора и
 * полоса с прямым выходом без объяснения.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "method_core.h"

/* Раскладка структур пришпилена числами по ОБЕ стороны границы: те же числа
 * стоят в тесте `раскладка_структур_пришпилена` внутри src/ffi.rs. Если поле
 * переедет или сменит тип, сломается одна из двух сторон — и сломается раньше,
 * чем Swift начнёт читать мусор из чужого смещения. */
_Static_assert(sizeof(mc_str_t) == 16, "раскладка mc_str_t разъехалась");
_Static_assert(sizeof(mc_buffer_t) == 16, "раскладка mc_buffer_t разъехалась");
_Static_assert(sizeof(mc_route_desc_t) == 96, "раскладка mc_route_desc_t разъехалась");
_Static_assert(sizeof(mc_lane_desc_t) == 168, "раскладка mc_lane_desc_t разъехалась");
_Static_assert(sizeof(mc_probe_t) == 40, "раскладка mc_probe_t разъехалась");
_Static_assert(sizeof(mc_action_t) == 40, "раскладка mc_action_t разъехалась");
_Static_assert(sizeof(mc_reason_t) == 24, "раскладка mc_reason_t разъехалась");
_Static_assert(sizeof(mc_score_t) == 32, "раскладка mc_score_t разъехалась");

/* ─────────────────────────── Оснастка ─────────────────────────── */

static int failures = 0;
static int checks = 0;

static void check(int ok, const char *what) {
    checks++;
    if (ok) {
        printf("  ok   %s\n", what);
    } else {
        failures++;
        printf("  ПРОВАЛ %s\n", what);
        mc_str_t err;
        if (mc_last_error(&err) == MC_OK && err.len > 0) {
            printf("         последняя ошибка: %.*s\n", (int)err.len, err.ptr);
        }
    }
}

static void must(int32_t rc, const char *what) {
    if (rc != MC_OK) {
        failures++;
        checks++;
        mc_str_t err, text;
        mc_status_text(rc, &text);
        printf("  ПРОВАЛ %s: код %d (%.*s)\n", what, rc, (int)text.len, text.ptr);
        if (mc_last_error(&err) == MC_OK && err.len > 0) {
            printf("         %.*s\n", (int)err.len, err.ptr);
        }
        exit(1);
    }
}

static mc_str_t S(const char *s) {
    mc_str_t r;
    r.ptr = (const uint8_t *)s;
    r.len = strlen(s);
    return r;
}

/* Шаг измерительного контура и он же шаг модельного времени. */
#define STEP_MS 5000u

static uint32_t add_route(mc_engine_t *e, const char *id, const char *node,
                          const char *transport, const char *country, int32_t axis) {
    mc_route_desc_t d;
    must(mc_route_desc_init(&d), "mc_route_desc_init");
    d.id = S(id);
    d.node = S(node);
    d.transport = S(transport);
    d.country = S(country);
    d.axis = axis;
    d.exposure = MC_EXPOSURE_TUNNELLED;
    uint32_t h = 0;
    must(mc_engine_add_route(e, &d, &h), id);
    return h;
}

static void probe_ok(mc_engine_t *e, uint32_t route, uint64_t at, float rtt) {
    mc_probe_t p;
    memset(&p, 0, sizeof p);
    p.route = route;
    p.outcome = MC_OUTCOME_OK;
    p.rtt_ms = rtt;
    p.at_ms = at;
    must(mc_engine_observe(e, &p), "проба (успех)");
}

static void probe_timeout(mc_engine_t *e, uint32_t route, uint64_t at) {
    mc_probe_t p;
    memset(&p, 0, sizeof p);
    p.route = route;
    p.outcome = MC_OUTCOME_TIMEOUT;
    p.at_ms = at;
    must(mc_engine_observe(e, &p), "проба (таймаут)");
}

/* Объявить каталог: три маршрута на разных осях и одна полоса. */
static void declare_catalog(mc_engine_t *e, uint32_t *hy2, uint32_t *grpc,
                            uint32_t *trojan, uint32_t *web) {
    *hy2 = add_route(e, "fi.hysteria2.443", "fi", "hysteria2", "FI", MC_AXIS_QUIC_UDP);
    *grpc = add_route(e, "lt.grpc.2083", "lt", "vless-grpc", "LT", MC_AXIS_FAKE_TLS_H2);
    *trojan = add_route(e, "us.trojan.2096", "us", "trojan", "US", MC_AXIS_REAL_TLS);

    mc_lane_desc_t l;
    must(mc_lane_desc_init(&l), "mc_lane_desc_init");
    l.id = S("web");
    l.title = S("Веб");
    l.sla = MC_SLA_BROWSE;
    must(mc_engine_add_lane(e, &l, web), "полоса web");
}

/* ─────────────────────────── Сценарий ─────────────────────────── */

#ifdef MC_HAVE_TEST_PANIC
/* Объявлена здесь, а не в method_core.h: в продуктовой сборке этого символа
 * нет вовсе. */
int32_t mc_test_panic(void);
#endif

int main(void) {
    printf("Версия ABI: %u\n", mc_abi_version());

#ifdef MC_HAVE_TEST_PANIC
    /* Паника внутри Rust обязана стать кодом возврата. Если бы профиль сборки
     * разворачивал её в abort, эта строка не выполнилась бы вовсе: процесс
     * умер бы целиком. */
    check(mc_test_panic() == MC_PANIC, "паника поймана на границе, а не уронила процесс");
#endif

    /* ── 1. Движок. ── */
    mc_engine_t *e = NULL;
    must(mc_engine_new(STEP_MS, &e), "mc_engine_new");
    check(e != NULL, "движок создан");

    /* Неудачный вызов не имеет права трогать чужую переменную: в ней лежит
       живой движок, и обнуление отняло бы единственную ссылку на него. */
    mc_engine_t *before = e;
    check(mc_engine_new(0, &e) == MC_INVALID_ARGUMENT,
          "нулевой шаг проб отвергнут (на него делят)");
    check(e == before, "неудачный вызов не затёр выходной параметр");

    /* ── 2. Каталог. ── */
    uint32_t hy2 = 0, grpc = 0, trojan = 0, web = 0;
    declare_catalog(e, &hy2, &grpc, &trojan, &web);

    size_t n = 0;
    must(mc_engine_route_count(e, &n), "route_count");
    check(n == 3, "объявлено три маршрута");
    must(mc_engine_lane_count(e, &n), "lane_count");
    check(n == 1, "объявлена одна полоса");

    /* Дескриптор ↔ строка в обе стороны. */
    uint32_t again = 0;
    must(mc_engine_route_handle(e, S("lt.grpc.2083"), &again), "route_handle");
    check(again == grpc, "дескриптор по идентификатору совпал");
    mc_str_t id;
    must(mc_engine_route_id(e, trojan, &id), "route_id");
    check(id.len == strlen("us.trojan.2096") &&
              memcmp(id.ptr, "us.trojan.2096", id.len) == 0,
          "идентификатор по дескриптору совпал");

    /* Отказы, которые обязаны быть отказами. */
    {
        mc_route_desc_t d;
        must(mc_route_desc_init(&d), "desc_init");
        d.id = S("fi.hysteria2.443");
        d.node = S("fi");
        d.transport = S("hysteria2");
        d.country = S("FI");
        d.axis = MC_AXIS_QUIC_UDP;
        uint32_t h = 0;
        check(mc_engine_add_route(e, &d, &h) == MC_DUPLICATE,
              "повторный идентификатор маршрута отвергнут");

        d.id = S("x.direct");
        d.exposure = MC_EXPOSURE_DIRECT;
        check(mc_engine_add_route(e, &d, &h) == MC_INVALID_ARGUMENT,
              "прямой выход на оси обхода отвергнут");
    }
    {
        mc_lane_desc_t l;
        must(mc_lane_desc_init(&l), "lane_desc_init");
        l.id = S("leaky");
        l.title = S("Дырявая");
        l.allow = (uint8_t)(MC_EXPOSURE_TUNNELLED | MC_EXPOSURE_DIRECT);
        uint32_t h = 0;
        check(mc_engine_add_lane(e, &l, &h) == MC_INVALID_ARGUMENT,
              "полоса с прямым выходом без объяснения отвергнута");
    }
    check(mc_engine_observe(e, NULL) == MC_NULL_POINTER, "NULL вместо пробы отвергнут");
    {
        mc_probe_t p;
        memset(&p, 0, sizeof p);
        p.route = 999;
        p.outcome = MC_OUTCOME_TIMEOUT;
        check(mc_engine_observe(e, &p) == MC_INVALID_HANDLE,
              "чужой дескриптор маршрута отвергнут");
    }

    /* ── 3. Девяносто раундов измерений. ──
     * Меньше нельзя: уверенность набирается из числа проб и ширины интервала
     * Уилсона, и порог участия в выборе (0.5) берётся примерно к этому месту.
     * Маленькая рябь в задержке — чтобы перцентили были перцентилями, а не
     * одним и тем же числом. */
    uint64_t now = 0;
    const int ROUNDS = 90;
    for (int i = 0; i < ROUNDS; i++) {
        now += STEP_MS;
        float ripple = (float)(i % 5);
        probe_ok(e, hy2, now, 40.0f + ripple);
        probe_ok(e, grpc, now, 65.0f + ripple);
        probe_ok(e, trojan, now, 90.0f + ripple);
    }

    /* ── 4. Первое решение. ── */
    mc_decision_t *d = NULL;
    must(mc_engine_reconcile(e, now, &d), "reconcile");

    size_t acts = 0;
    must(mc_decision_action_count(d, &acts), "action_count");
    check(acts == 1, "ровно одно действие на первом решении");

    mc_action_t a;
    must(mc_decision_action(d, 0, &a), "action[0]");
    check(a.kind == MC_ACTION_SELECT, "действие — назначить маршрут полосе");
    check(a.lane == web, "действие про полосу web");
    check(a.route == hy2, "выбран самый быстрый маршрут");
    check(a.reason_kind == MC_REASON_INITIAL, "род причины — первый выбор");
    check(a.reason.len > 0, "причина не пуста");
    printf("       причина: %.*s\n", (int)a.reason.len, a.reason.ptr);

    size_t reasons = 0;
    must(mc_decision_reason_count(d, &reasons), "reason_count");
    check(reasons >= 1, "журнал причин не пуст");

    /* Выход за границу списка — ошибка, а не мусор. */
    check(mc_decision_action(d, acts, &a) == MC_INVALID_ARGUMENT,
          "запрос действия за границей списка отвергнут");

    must(mc_engine_lane_applied(e, web, hy2), "подтвердить применение");
    mc_decision_free(d);
    d = NULL;

    uint32_t current = 0;
    must(mc_engine_lane_current(e, web, &current), "lane_current");
    check(current == hy2, "движок запомнил подтверждённый маршрут");

    /* ── 5. Смерть активного маршрута. ──
     * Три пробы подряд без ответа — это ФАКТ, а не число. Он обходит порог,
     * выдержку и остывание целиком: оценка не имеет права удержать человека
     * на мёртвом маршруте. */
    for (int i = 0; i < 3; i++) {
        now += STEP_MS;
        probe_timeout(e, hy2, now);
        probe_ok(e, grpc, now, 65.0f);
        probe_ok(e, trojan, now, 90.0f);
    }

    must(mc_engine_reconcile(e, now, &d), "reconcile после смерти маршрута");
    must(mc_decision_action_count(d, &acts), "action_count после смерти");
    check(acts == 2, "переключение плюс обрыв потоков");

    must(mc_decision_action(d, 0, &a), "action[0] после смерти");
    check(a.kind == MC_ACTION_SELECT, "первое действие — назначить маршрут");
    check(a.route != hy2, "движок ушёл с мёртвого маршрута");
    check(a.route == grpc, "ушёл на лучший из живых, и он на ДРУГОЙ оси");
    check(a.reason_kind == MC_REASON_EMERGENCY_FACT, "причина — факт, а не сравнение чисел");
    printf("       причина: %.*s\n", (int)a.reason.len, a.reason.ptr);

    mc_action_t a2;
    must(mc_decision_action(d, 1, &a2), "action[1] после смерти");
    check(a2.kind == MC_ACTION_DRAIN, "второе действие — оборвать живые соединения");
    check(a2.reason_kind == MC_REASON_NONE, "у обрыва своей причины нет");

    must(mc_engine_lane_applied(e, web, a.route), "подтвердить аварийное применение");
    mc_decision_free(d);
    d = NULL;

    /* ── 6. Оценка. ── */
    mc_score_t s_live, s_dead;
    must(mc_engine_score(e, grpc, MC_SLA_BROWSE, now, &s_live), "score живого");
    must(mc_engine_score(e, hy2, MC_SLA_BROWSE, now, &s_dead), "score мёртвого");
    printf("       живой: %.1f (уверенность %.2f, ограничивает метрика %d, ворота 0x%x)\n",
           (double)s_live.value, (double)s_live.confidence, s_live.limiter, s_live.gates);
    printf("       мёртвый: %.1f (ворота 0x%x)\n", (double)s_dead.value, s_dead.gates);
    check(s_live.gates == 0, "живой маршрут ворота проходит");
    check(s_live.value > 0.0f, "у живого маршрута есть оценка");
    check(s_live.confidence > 0.5f, "живому маршруту можно верить");
    check(s_live.display == MC_DISPLAY_VALUE, "уверенности хватает, чтобы показать число");
    check(s_dead.gates != 0, "мёртвый маршрут дисквалифицирован воротами");
    check(s_dead.value == 0.0f, "дисквалификация — это ровно ноль, а не низкая оценка");
    check(mc_engine_score(e, grpc, 777, now, &s_live) == MC_INVALID_ARGUMENT,
          "несуществующий класс нагрузки отвергнут");

    /* ── 7. Состояние переживает перезапуск. ── */
    mc_buffer_t state;
    memset(&state, 0, sizeof state);
    must(mc_engine_save_state(e, now, &state), "save_state");
    check(state.len > 0 && state.data != NULL, "состояние выгружено");
    printf("       состояние: %zu байт\n", state.len);

    mc_engine_free(e);
    e = NULL;

    /* Новый движок, тот же каталог — и сравнение «с историей» против «без». */
    mc_engine_t *fresh = NULL;
    must(mc_engine_new(STEP_MS, &fresh), "движок без истории");
    uint32_t f_hy2, f_grpc, f_trojan, f_web;
    declare_catalog(fresh, &f_hy2, &f_grpc, &f_trojan, &f_web);

    mc_engine_t *restored = NULL;
    must(mc_engine_new(STEP_MS, &restored), "движок с историей");
    uint32_t r_hy2, r_grpc, r_trojan, r_web;
    declare_catalog(restored, &r_hy2, &r_grpc, &r_trojan, &r_web);
    must(mc_engine_load_state(restored, state.data, state.len, now, 1000),
         "load_state");

    mc_score_t s_fresh, s_restored;
    must(mc_engine_score(fresh, f_grpc, MC_SLA_BROWSE, now, &s_fresh), "score без истории");
    must(mc_engine_score(restored, r_grpc, MC_SLA_BROWSE, now, &s_restored), "score с историей");
    check(s_fresh.confidence == 0.0f, "без истории уверенности нет вовсе");
    check(s_fresh.display == MC_DISPLAY_MEASURING, "без истории показывать нечего");
    check(s_restored.confidence > 0.5f, "история пережила перезапуск");
    printf("       уверенность: без истории %.2f, с историей %.2f\n",
           (double)s_fresh.confidence, (double)s_restored.confidence);

    /* Битое состояние — ошибка, а не паника. */
    {
        const uint8_t junk[] = {'{', 'n', 'o', 't', ' ', 'j', 's', 'o', 'n'};
        check(mc_engine_load_state(restored, junk, sizeof junk, now, 0) == MC_STATE_INVALID,
              "битое состояние отвергнуто");
        check(mc_engine_load_state(restored, NULL, 0, now, 0) == MC_STATE_INVALID,
              "пустое состояние отвергнуто");
    }

    /* ── 8. Освобождение. Строки решения умирают вместе с решением, буфер —
     *     своей функцией, движок — своей. Двойной вызов безобиден. ── */
    mc_buffer_free(&state);
    check(state.data == NULL && state.len == 0, "буфер обнулён после освобождения");
    mc_buffer_free(&state);
    mc_decision_free(NULL);
    mc_engine_free(fresh);
    mc_engine_free(restored);
    mc_engine_free(NULL);

    printf("\nПроверок: %d, провалов: %d\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
