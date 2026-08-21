// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Runtime.Versioning;
using System.Security.Principal;

namespace Method.App.Services;

/// <summary>Есть ли у процесса права администратора.</summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    /// <summary>
    /// Истина и для SYSTEM: его токен содержит группу администраторов, а именно
    /// под ним привилегированная часть и работает в обычной жизни.
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
