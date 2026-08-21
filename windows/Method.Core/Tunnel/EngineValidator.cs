// Copyright (C) 2026  Kovalyov Georgiy
// This file is part of Method.
//
// Method is free software: you can redistribute it and/or modify it under the
// terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. See LICENSE for details.

using System.Diagnostics;

namespace Method.Core.Tunnel;

/// <summary>
/// Проверка собранной конфигурации через <c>sing-box check</c> перед запуском.
///
/// Проверка необходимая, но не достаточная: <c>check</c> ловит битый JSON,
/// неизвестный тип outbound и неполный outbound, но пропускает, например,
/// незнакомое значение стека TUN — оно падает уже в рантайме. Поэтому запуск
/// после успешной проверки всё равно подтверждается тем, что процесс прожил
/// первые секунды.
/// </summary>
public static class EngineValidator
{
    public sealed record Result(bool Ok, string? Output)
    {
        public string? Error => Ok ? null : Output;
    }

    public static async Task<Result> ValidateAsync(
        string configJson,
        string? enginePath = null,
        CancellationToken ct = default)
    {
        enginePath ??= AppPaths.ResolveEngine();
        if (!File.Exists(enginePath))
            return new Result(false, $"движок не найден: {enginePath}");

        var dir = ResolveCheckDirectory();
        var configPath = Path.Combine(dir, $"check-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(configPath, configJson, ct).ConfigureAwait(false);
            return await RunCheckAsync(enginePath, configPath, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new Result(false, $"проверить конфигурацию не удалось: {e.Message}");
        }
        finally
        {
            try { File.Delete(configPath); } catch { /* уже удалён или занят */ }
        }
    }

    /// <summary>
    /// Куда класть временный файл конфигурации.
    ///
    /// В нём лежат пароли, секреты обфускации и ключи Reality — пусть и на доли
    /// секунды. Общий временный каталог для этого плохо подходит: под SYSTEM это
    /// каталог Windows, куда могут писать и другие пользователи машины. Поэтому
    /// сначала пробуем собственный служебный каталог с урезанными правами и только
    /// потом откатываемся на временный.
    /// </summary>
    private static string ResolveCheckDirectory()
    {
        try
        {
            var dir = Path.Combine(AppPaths.ProgramDataDir, "check");
            Directory.CreateDirectory(dir);

            // Право создать каталог ещё не означает право писать в него файлы —
            // проверяем делом.
            var probe = Path.Combine(dir, $"probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), AppPaths.ProductName + "-check");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static async Task<Result> RunCheckAsync(string enginePath, string configPath, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(enginePath, ["check", "-c", configPath])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null) return new Result(false, "запустить проверку не удалось");

        // Оба потока читаются одновременно: последовательное чтение может встать
        // намертво, если второй буфер переполнится.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            return new Result(false, "проверка не ответила за 10 с");
        }

        var output = string.Join(
            Environment.NewLine,
            new[] { await Safe(stdout), await Safe(stderr) }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new Result(process.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? null : output);
    }

    private static async Task<string> Safe(Task<string> task)
    {
        try { return await task.ConfigureAwait(false) ?? ""; }
        catch { return ""; }
    }
}
