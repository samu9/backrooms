using Backrooms.Core;
using Godot;

namespace Backrooms;

/// <summary>
/// Placeholder entry point (M1): proves the Godot project builds against Core and that the
/// generator runs in the engine. M2 replaces it with chunk streaming and a player.
/// </summary>
public partial class Main : Node3D
{
    public override void _Ready()
    {
        int seed = 1;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--seed=") && int.TryParse(arg["--seed=".Length..], out var s)) seed = s;

        var gen = new Gen(seed);
        int walls = 0;
        for (int gz = -64; gz < 64; gz++)
            for (int gx = -64; gx < 64; gx++)
                if (gen.Solid(gx, gz)) walls++;
        var (x, z) = gen.FindFree(0, 0);
        GD.Print($"Backrooms seed {seed}: {walls} wall cells in the 128x128 around the origin, spawn at ({x}, {z}), zone {gen.ZoneAt(0, 0).Type}");

        var label = GetNodeOrNull<Label>("UI/Info");
        if (label != null) label.Text = $"seed {seed} · {walls} wall cells · spawn ({x}, {z})";
    }
}
