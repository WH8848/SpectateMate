using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SpectateMate.Core;

/// <summary>
/// Keeps track of the teammates we have seen avatars for, so the mod can pick a
/// sensible auto-spectate target without walking the node tree.
/// </summary>
internal static class SpectateRegistry
{
    private static readonly List<Player> Teammates = new();

    public static void Register(Player player)
    {
        if (!Teammates.Contains(player))
        {
            Teammates.Add(player);
        }
    }

    public static void Remember(Player player)
    {
        if (!Teammates.Contains(player))
        {
            Teammates.Add(player);
        }
    }

    public static Player? PickTarget()
    {
        foreach (Player player in Teammates)
        {
            try
            {
                _ = player.NetId;
                return player;
            }
            catch
            {
                // disposed player object, skip
            }
        }

        return null;
    }

    public static IReadOnlyList<Player> All => Teammates;

    public static void Clear()
    {
        Teammates.Clear();
    }
}
