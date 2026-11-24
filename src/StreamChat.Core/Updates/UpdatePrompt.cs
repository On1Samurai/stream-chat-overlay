namespace StreamChat.Core.Updates;

/// <summary>Откуда пришла найденная версия.</summary>
public enum UpdateCheckSource { FirstAfterStart, Daily, Manual }

/// <summary>Когда предлагать обновление. Состояние одного запуска; только поток интерфейса.</summary>
public sealed class UpdatePrompt
{
    private string? _version;
    private UpdateCheckSource _source;
    private string? _postponed;
    private bool _asked;
    private string? _askedVersion; // версия, которую увидел пользователь

    public void Found(string version, UpdateCheckSource source)
    {
        // «Позже» молчит о той же версии, пока пользователь сам не проверит
        if (source == UpdateCheckSource.Manual) _postponed = null;
        else if (version == _postponed) return;

        // повторная находка не ослабляет источник: первая после запуска остаётся первой
        if (version != _version || source == UpdateCheckSource.Manual) _source = source;
        _version = version;
    }

    /// <summary>«Позже»: откладывает показанную версию. Без открытого окна ничего не делает.</summary>
    public void Postpone()
    {
        if (!_asked) return; // повторный вызов (кнопка и закрытие окна) не трогает остальное
        var target = _askedVersion;
        _asked = false;
        _askedVersion = null;
        if (target is null) return;
        _postponed = target;
        if (_version == target) _version = null;
    }

    public string? ShouldAsk(bool checksEnabled, bool obsEnabled, bool? obsIdle)
    {
        if (!checksEnabled || _version is null || _asked) return null;
        // без OBS спрашиваем при запуске и по просьбе; с OBS только когда нет ни эфира, ни записи,
        // а по просьбе — пока OBS не сообщает об эфире или записи
        var allowed = obsEnabled
            ? _source == UpdateCheckSource.Manual ? obsIdle != false : obsIdle == true
            : _source != UpdateCheckSource.Daily;
        // отказ на эфире снимает разрешение ручной проверки: устаревшие данные OBS не должны его вернуть
        if (obsEnabled && _source == UpdateCheckSource.Manual && obsIdle == false) _source = UpdateCheckSource.FirstAfterStart;
        return allowed ? _version : null;
    }

    public void Asked()
    {
        _asked = true;
        _askedVersion = _version;
    }

    /// <summary>Окно закрыто без обновления: то же, что «Позже» для показанной версии.</summary>
    public void Closed() => Postpone();

    /// <summary>Окно спрятано из-за эфира или записи: вопрос вернётся.</summary>
    public void Hidden()
    {
        _asked = false;
        _askedVersion = null;
        // эфир начался: разрешение ручной проверки не переживает показ, дальше нужен idle OBS
        if (_source == UpdateCheckSource.Manual) _source = UpdateCheckSource.FirstAfterStart;
    }
}
