# Ресурсы движка

Сюда `Scripts/bootstrap.ps1` кладёт две вещи, которых **нет в репозитории**:

| Файл | Версия | Откуда |
|---|---|---|
| `sing-box.exe` | 1.13.13 | релиз [SagerNet/sing-box](https://github.com/SagerNet/sing-box/releases), архив `sing-box-1.13.13-windows-amd64.zip` |
| `wintun.dll` | 0.14.1 | [wintun.net](https://www.wintun.net/), каталог `bin/amd64` |

Версия sing-box совпадает с той, что берёт `macos/Scripts/bootstrap.sh`.
Расхождение версий между платформами означает расхождение в поведении
туннеля — а его потом ищут в клиенте.

`wintun.dll` обязателен и должен лежать рядом с `sing-box.exe`: без него
TUN-адаптер не поднимется вовсе. В готовой поставке обе вещи вшиты в
`Method.exe` ресурсами и извлекаются при установке.
