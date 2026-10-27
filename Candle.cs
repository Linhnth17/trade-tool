namespace BtcSignalBot;

public sealed record Candle(
    DateTimeOffset OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume);
