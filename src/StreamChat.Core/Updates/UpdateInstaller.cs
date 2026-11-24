using System.IO.Compression;

namespace StreamChat.Core.Updates;

/// <summary>Откат не удался: рабочая копия осталась только как StreamChat.old.exe. Cleanup после этого запускать нельзя.</summary>
public sealed class UpdateRollbackException(string exeName, Exception inner)
    : IOException($"Программа сохранена как StreamChat.old.exe. Переименуйте её обратно в {exeName}.", inner);

/// <summary>Замена exe рядом с самим exe. Любой сбой оставляет рабочий exe по исходному пути.</summary>
public static class UpdateInstaller
{
    private const string ExeEntry = "StreamChat/StreamChat.exe";
    private static readonly string[] GuideFiles = ["ИНСТРУКЦИЯ.txt", "LICENSES.txt"];

    private static string Side(string exePath, string name) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath))!, name);

    /// <summary>Сообщение исключения показывается пользователю: путей в нём нет.</summary>
    public static void RejectSideName(string exePath)
    {
        var n = Path.GetFileName(exePath);
        if (n.Equals("StreamChat.new.exe", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("StreamChat.old.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Переименуйте программу в StreamChat.exe и повторите обновление.");
    }

    /// <summary>Кладёт StreamChat.new.exe рядом с exe; справочные файлы обновляет, только если они уже лежат рядом.</summary>
    public static string Extract(string zipPath, string exePath)
    {
        RejectSideName(exePath);
        var newExe = Side(exePath, "StreamChat.new.exe");
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var exes = zip.Entries.Where(e => e.FullName == ExeEntry).ToList();
            if (exes.Count != 1) throw new InvalidDataException("В архиве должен быть ровно один StreamChat.exe.");
            exes[0].ExtractToFile(newExe, true);

            foreach (var name in GuideFiles)
            {
                var target = Side(exePath, name);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName == "StreamChat/" + name);
                if (entry == null || !File.Exists(target)) continue;
                // Справка не должна ломать обновление: пишем во временный файл, старая остаётся при сбое.
                var tmp = target + ".tmp";
                try { entry.ExtractToFile(tmp, true); File.Move(tmp, target, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            return newExe;
        }
        catch
        {
            try { File.Delete(newExe); } catch { }
            throw;
        }
    }

    /// <summary>exe → .old.exe, .new.exe → exe. При сбое возвращает exe на место и удаляет .new.exe.</summary>
    public static void Swap(string exePath)
    {
        RejectSideName(exePath);
        var newExe = Side(exePath, "StreamChat.new.exe");
        var oldExe = Side(exePath, "StreamChat.old.exe");
        var movedAway = false;
        try
        {
            if (!File.Exists(newExe)) throw new FileNotFoundException("Нет скачанного exe.");
            if (File.Exists(oldExe)) File.Delete(oldExe);
            File.Move(exePath, oldExe);
            movedAway = true;
            File.Move(newExe, exePath);
        }
        catch (Exception ex)
        {
            var restored = !movedAway || File.Exists(exePath) || TryRollback(oldExe, exePath);
            try { File.Delete(newExe); } catch { }
            if (!restored) throw new UpdateRollbackException(Path.GetFileName(exePath), ex);
            throw;
        }
    }

    private static bool TryRollback(string oldExe, string exePath)
    {
        for (var i = 0; i < 3; i++)
        {
            try { File.Move(oldExe, exePath); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        return false;
    }

    /// <summary>Новый exe не запустился: возвращает прежний. Без .old.exe ничего не трогает.</summary>
    public static void Restore(string exePath)
    {
        var oldExe = Side(exePath, "StreamChat.old.exe");
        if (File.Exists(oldExe)) File.Move(oldExe, exePath, true);
    }

    public static void Cleanup(string exePath)
    {
        // Без exe под своим именем .old.exe — единственная рабочая копия.
        var keepOld = !File.Exists(exePath);
        foreach (var name in new[] { "StreamChat.old.exe", "StreamChat.new.exe", "StreamChat.download.zip", "StreamChat.download.zip.sig" })
            if (!(keepOld && name == "StreamChat.old.exe")) try { File.Delete(Side(exePath, name)); } catch { }
    }
}
