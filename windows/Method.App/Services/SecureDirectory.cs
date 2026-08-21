// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Method.App.Services;

/// <summary>
/// Явные права на каталог, из которого SYSTEM исполняет код.
///
/// <para><b>Зачем.</b> Каталог, созданный обычным способом, наследует права от
/// корня тома, а там обычным пользователям выдана запись — и это верно не только
/// для несистемного диска. В нашем каталоге лежит движок, который запускает
/// привилегированная часть от SYSTEM. Значит любой непривилегированный процесс мог
/// бы подменить этот файл и получить выполнение своего кода от SYSTEM после
/// ближайшего входа в систему — без запроса прав и без единого следа.</para>
///
/// <para>Рядом лежит конфигурация с паролями и ключами. Её точно так же можно было
/// бы прочитать и подменить, то есть увести весь трафик на чужой сервер.</para>
///
/// <para><b>Как.</b> Наследование снимается, а список прав задаётся целиком заново.
/// Именно целиком: точечная выдача прав нужным учётным записям оставила бы на месте
/// запись, которую когда-то выдали руками, — и та пережила бы «успешную» настройку.
/// Права задаются по идентификаторам, а не по именам: на локализованной Windows
/// группа называется иначе, и правило с английским именем молча не применилось бы.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecureDirectory
{
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier Users =
        new(WellKnownSidType.BuiltinUsersSid, null);

    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Урезать права каталога и проверить, что получилось. Проверка обязательна:
    /// коды возврата тут ничего не доказывают, а «настроенный» каталог с чужой
    /// записью в списке — это дыра, о которой никто не узнает.
    /// </summary>
    /// <returns>Пусто, если всё вышло; иначе описание того, что не так.</returns>
    public static string? Harden(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return "каталога нет";

            var info = new DirectoryInfo(path);
            var security = new DirectorySecurity();

            // Снимаем наследование и не переносим унаследованные записи: именно
            // оттуда приходит запись для всех подряд.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            security.AddAccessRule(new FileSystemAccessRule(
                Administrators, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                LocalSystem, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            // Чтение обычным пользователям оставлено намеренно: без него человек не
            // прочитает журнал, чтобы приложить его к обращению. Записи здесь нет.
            security.AddAccessRule(new FileSystemAccessRule(
                Users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));

            info.SetAccessControl(security);

            return Verify(path, [Administrators, LocalSystem, Users]);
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>
    /// Закрыть файл от всех, кроме администраторов и SYSTEM. Для конфигурации:
    /// в ней пароли, ключи Reality и секреты обфускации, и читать её обычному
    /// пользователю незачем — интерфейс собирает конфигурацию сам и передаёт её
    /// по каналу, а не через файл.
    /// </summary>
    public static string? RestrictFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return "файла нет";

            var info = new FileInfo(path);
            var security = new FileSecurity();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));

            info.SetAccessControl(security);

            return VerifyFile(path, [Administrators, LocalSystem]);
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static string? Verify(string path, SecurityIdentifier[] allowed)
    {
        var rules = new DirectoryInfo(path)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        return FindExtra(rules, allowed);
    }

    private static string? VerifyFile(string path, SecurityIdentifier[] allowed)
    {
        var rules = new FileInfo(path)
            .GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        return FindExtra(rules, allowed);
    }

    private static string? FindExtra(AuthorizationRuleCollection rules, SecurityIdentifier[] allowed)
    {
        var extra = new List<string>();

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid) continue;
            if (allowed.Contains(sid)) continue;

            extra.Add(sid.Value);
        }

        return extra.Count == 0 ? null : $"остались лишние учётные записи: {string.Join(", ", extra)}";
    }
}
