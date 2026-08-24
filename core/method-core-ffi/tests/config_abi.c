/*
 * config_abi.c — проверка слоя конфигурации НАСТОЯЩИМ компилятором C.
 *
 * Тест на Rust доказал бы только то, что Rust согласен сам с собой. Здесь
 * структуры берутся из include/method_core.h, и если заголовок разъедется с
 * библиотекой, сломается ровно так же, как сломался бы Swift или JNI.
 *
 * Сценарий сквозной, как у человека:
 *   1. разобрать одну ссылку;
 *   2. разобрать тело подписки, где одна строка кривая;
 *   3. собрать конфиг полос и убедиться, что блокировка на месте, а прямого
 *      выхода в полосе остатка нет;
 *   4. проверить, что негодная политика ОТКАЗЫВАЕТСЯ собираться;
 *   5. всё освободить.
 */

#include <stdio.h>
#include <string.h>
#include <stdlib.h>

#include "method_core.h"

static int провалов = 0;

static void проверить(int условие, const char *что) {
    if (условие) {
        printf("  ок   %s\n", что);
    } else {
        printf("  СБОЙ %s\n", что);
        провалов++;
    }
}

static mc_str_t строка(const char *s) {
    mc_str_t v;
    v.ptr = (const uint8_t *)s;
    v.len = strlen(s);
    return v;
}

/* Содержит ли выданный буфер подстроку. Буфер не завершён нулём — работаем по длине. */
static int содержит(const mc_buffer_t *b, const char *needle) {
    if (b->data == NULL || b->len == 0) return 0;
    size_t n = strlen(needle);
    if (n > b->len) return 0;
    for (size_t i = 0; i + n <= b->len; i++) {
        if (memcmp(b->data + i, needle, n) == 0) return 1;
    }
    return 0;
}

int main(void) {
    mc_buffer_t buf = {NULL, 0};

    /* 1. Одна ссылка. */
    int32_t rc = mc_config_parse_link(
        строка("hysteria2://shared:pw@1.2.3.4:443/?sni=www.microsoft.com&insecure=1#US"), &buf);
    проверить(rc == MC_OK, "ссылка разобрана");
    проверить(содержит(&buf, "\"hysteria2\""), "протокол определён");
    проверить(содержит(&buf, "1.2.3.4"), "узел на месте");
    mc_buffer_free(&buf);

    /* 2. Тело подписки: одна строка намеренно кривая. */
    rc = mc_config_parse_many(
        строка("hysteria2://pw@1.2.3.4:443#A\n"
               "мусор\n"
               "vless://u@1.2.3.4:2083?security=reality&pbk=K&sni=a.com&type=grpc&serviceName=gs#B"),
        &buf);
    проверить(rc == MC_OK, "подписка разобрана");
    проверить(содержит(&buf, "\"errors\""), "ошибки отданы отдельно");
    проверить(содержит(&buf, "\"line\":2"), "номер кривой строки назван");
    mc_buffer_free(&buf);

    /* 3. Конфиг полос из четырёх узлов на трёх осях. */
    const char *профили =
        "[{\"name\":\"A\",\"protocol\":\"hysteria2\",\"host\":\"1.2.3.4\",\"port\":443,"
        "\"parameters\":{\"kind\":\"hysteria2\",\"hysteria2\":{\"password\":\"p\",\"alpn\":[\"h3\"]}}},"
        "{\"name\":\"B\",\"protocol\":\"trojan\",\"host\":\"a.example.com\",\"port\":8443,"
        "\"parameters\":{\"kind\":\"trojan\",\"trojan\":{\"password\":\"p\"}}}]";
    const char *настройки = "{\"clashSecret\":\"s\"}";
    rc = mc_config_build_lanes(строка(профили), строка(""), строка(настройки), &buf);
    проверить(rc == MC_OK, "конфиг полос собран");
    проверить(содержит(&buf, "OUT_block"), "блокировка объявлена");
    проверить(содержит(&buf, "\"AX.quic-udp\""), "ось QUIC собрана");
    /* Полоса остатка начинается с оси и не содержит прямого выхода вовсе:
       увести трафик открытым через API нельзя. */
    проверить(содержит(&buf, "\"L.web\":[\"AX"), "полоса остатка ведёт на оси");
    проверить(!содержит(&buf, "\"L.web\":[\"OUT_direct"),
              "прямого выхода в полосе остатка нет");
    mc_buffer_free(&buf);

    /* 4. Негодная политика: полоса остатка выпускает трафик открытым. */
    const char *плохая =
        "{\"version\":1,\"lanes\":[{\"id\":\"web\",\"title\":\"В\",\"allowsDirect\":true,"
        "\"justification\":\"зачем-то\",\"minAxes\":1}],\"flows\":[],\"defaultLane\":\"web\"}";
    rc = mc_config_build_lanes(строка(профили), строка(плохая), строка(настройки), &buf);
    проверить(rc != MC_OK, "негодная политика отвергнута, а не собрана");

    printf(провалов == 0 ? "\nграница слоя конфигурации: без замечаний\n"
                         : "\nграница слоя конфигурации: ЕСТЬ СБОИ\n");
    return провалов == 0 ? 0 : 1;
}
