namespace EChat.Core.Models;

public static class GroupPalette
{
    public static readonly string[] Colors =
    [
        "#5b8fd9", "#e07a5f", "#81b29a", "#9b5de5",
        "#00bbf9", "#f15bb5", "#00f5d4", "#d4a373",
        "#a8dadc", "#e9c46a", "#2a9d8f", "#e76f51",
        "#606c38", "#99c2b2", "#f4511e", "#7CB342"
    ];

    public static string PickColor(string seed)
    {
        var hash = 0;
        foreach (var c in seed) hash = (hash * 31 + c) & 0x7FFFFFFF;
        return Colors[hash % Colors.Length];
    }
}