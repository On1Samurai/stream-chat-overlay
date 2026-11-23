namespace StreamChat.Core.Obs;

public enum MicState { None, On, Muted, NotInScene, Unknown, Partial }

public enum StatusLevel { Neutral, Good, Warning, Bad }

public sealed record StatusItem(string Label, string Text, StatusLevel Level);

public sealed record ObsSnapshot(
    bool Streaming, bool Reconnecting, TimeSpan Duration, long Skipped, long Total,
    bool Recording, bool RecordPaused, MicState Mic);
