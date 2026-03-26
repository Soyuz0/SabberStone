using System.Text.Json;
using SabberStoneBasicAI.Nodes;
using SabberStoneBasicAI.Score;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;

namespace SabberStoneBridge;

internal static class Program
{
    private static readonly BoardReconstructor Reconstructor = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static int _thinkDepth = 10;
    private static int _thinkWidth = 14;

    private static void Main()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();

        while (true)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            if (line == "QUIT")
            {
                break;
            }

            if (line.StartsWith("THINK_DEPTH:", StringComparison.Ordinal))
            {
                ParseThinkDepth(line["THINK_DEPTH:".Length..]);
                continue;
            }

            if (line.StartsWith("THINK_WIDTH:", StringComparison.Ordinal))
            {
                ParseThinkWidth(line["THINK_WIDTH:".Length..]);
                continue;
            }

            if (line.StartsWith("BOARD:", StringComparison.Ordinal))
            {
                HandleBoard(line["BOARD:".Length..]);
                continue;
            }

            WriteError($"Unknown command '{line}'.");
        }
    }

    private static void ParseThinkDepth(string raw)
    {
        if (!int.TryParse(raw.Trim(), out var value) || value < 1)
        {
            WriteError("THINK_DEPTH must be a positive integer.");
            return;
        }

        _thinkDepth = value;
    }

    private static void ParseThinkWidth(string raw)
    {
        if (!int.TryParse(raw.Trim(), out var value) || value < 1)
        {
            WriteError("THINK_WIDTH must be a positive integer.");
            return;
        }

        _thinkWidth = value;
    }

    private static void HandleBoard(string payload)
    {
        try
        {
            var boardState = JsonSerializer.Deserialize<BoardState>(payload, JsonOptions)
                             ?? throw new InvalidOperationException("Invalid board payload.");

            var game = Reconstructor.Reconstruct(boardState);
            var scoring = new MidRangeScore();

            var solutions = OptionNode.GetSolutions(game, game.CurrentPlayer.Id, scoring, _thinkDepth, _thinkWidth);
            if (solutions.Count == 0)
            {
                WriteJson(new Dictionary<string, object?>
                {
                    ["actions"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["type"] = "end_turn" },
                    },
                    ["score"] = int.MinValue,
                });
                return;
            }

            var best = solutions.OrderByDescending(s => s.Score).First();
            var tasks = new List<PlayerTask>();
            best.PlayerTasks(ref tasks);

            var actions = EncodeActions(tasks);

            WriteJson(new Dictionary<string, object?>
            {
                ["actions"] = actions,
                ["score"] = best.Score,
            });
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
        }
    }

    private static List<Dictionary<string, object?>> EncodeActions(List<PlayerTask> tasks)
    {
        var encoded = new List<Dictionary<string, object?>>();

        foreach (var task in tasks)
        {
            switch (task)
            {
                case PlayCardTask playCard when playCard.Source is not null:
                {
                    var (targetIndex, targetSide, targetZonePos, targetCardId) = EncodeTarget(playCard.Controller, playCard.Target);

                    var action = new Dictionary<string, object?>
                    {
                        ["type"] = "play_card",
                        ["hand_index"] = EncodeZonePositionAsIndex(playCard.Source.ZonePosition),
                        ["source_zone_pos"] = playCard.Source.ZonePosition,
                        ["target_index"] = targetIndex ?? -1,
                        ["position"] = playCard.ZonePosition,
                        ["card_id"] = playCard.Source.Card.Id,
                    };
                    if (targetSide is not null)
                    {
                        action["target_side"] = targetSide;
                    }
                    if (targetZonePos.HasValue)
                    {
                        action["target_zone_pos"] = targetZonePos.Value;
                    }
                    if (!string.IsNullOrEmpty(targetCardId))
                    {
                        action["target_card_id"] = targetCardId;
                    }

                    encoded.Add(action);
                    break;
                }

                case MinionAttackTask minionAttack when minionAttack.Source is not null:
                {
                    var (targetIndex, targetSide, targetZonePos, targetCardId) = EncodeTarget(minionAttack.Controller, minionAttack.Target);

                    var action = new Dictionary<string, object?>
                    {
                        ["type"] = "attack",
                        ["attacker_index"] = EncodeZonePositionAsIndex(minionAttack.Source.ZonePosition),
                        ["source_zone_pos"] = minionAttack.Source.ZonePosition,
                        ["target_index"] = targetIndex ?? -1,
                        ["card_id"] = minionAttack.Source.Card.Id,
                    };
                    if (targetSide is not null)
                    {
                        action["target_side"] = targetSide;
                    }
                    if (targetZonePos.HasValue)
                    {
                        action["target_zone_pos"] = targetZonePos.Value;
                    }
                    if (!string.IsNullOrEmpty(targetCardId))
                    {
                        action["target_card_id"] = targetCardId;
                    }

                    encoded.Add(action);
                    break;
                }

                case HeroAttackTask heroAttack:
                {
                    var (targetIndex, targetSide, targetZonePos, targetCardId) = EncodeTarget(heroAttack.Controller, heroAttack.Target);

                    var action = new Dictionary<string, object?>
                    {
                        ["type"] = "attack",
                        ["attacker_index"] = -1,
                        ["target_index"] = targetIndex ?? -1,
                        ["card_id"] = heroAttack.Controller.Hero.Card.Id,
                    };
                    if (targetSide is not null)
                    {
                        action["target_side"] = targetSide;
                    }
                    if (targetZonePos.HasValue)
                    {
                        action["target_zone_pos"] = targetZonePos.Value;
                    }
                    if (!string.IsNullOrEmpty(targetCardId))
                    {
                        action["target_card_id"] = targetCardId;
                    }

                    encoded.Add(action);
                    break;
                }

                case HeroPowerTask heroPower:
                {
                    var (targetIndex, targetSide, targetZonePos, targetCardId) = EncodeTarget(heroPower.Controller, heroPower.Target);

                    var action = new Dictionary<string, object?>
                    {
                        ["type"] = "hero_power",
                        ["target_index"] = targetIndex ?? -1,
                    };
                    if (targetSide is not null)
                    {
                        action["target_side"] = targetSide;
                    }
                    if (targetZonePos.HasValue)
                    {
                        action["target_zone_pos"] = targetZonePos.Value;
                    }
                    if (!string.IsNullOrEmpty(targetCardId))
                    {
                        action["target_card_id"] = targetCardId;
                    }

                    encoded.Add(action);
                    break;
                }

                case EndTurnTask:
                    encoded.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "end_turn",
                    });
                    break;
            }
        }

        if (encoded.All(a => !string.Equals(a["type"] as string, "end_turn", StringComparison.Ordinal)))
        {
            encoded.Add(new Dictionary<string, object?>
            {
                ["type"] = "end_turn",
            });
        }

        return encoded;
    }

    private static (int? TargetIndex, string? TargetSide, int? TargetZonePos, string? TargetCardId) EncodeTarget(Controller actor, ICharacter? target)
    {
        if (target is null)
        {
            return (null, null, null, null);
        }

        var side = target.Controller == actor ? "player" : "opponent";
        var targetCardId = target.Card?.Id;
        return target switch
        {
            Hero => (-1, side, null, targetCardId),
            Minion minion => (EncodeZonePositionAsIndex(minion.ZonePosition), side, minion.ZonePosition, targetCardId),
            _ => (null, null, null, targetCardId),
        };
    }

    private static int EncodeZonePositionAsIndex(int zonePosition)
    {
        if (zonePosition <= 0)
        {
            return zonePosition;
        }
        return zonePosition - 1;
    }

    private static void WriteError(string message)
    {
        WriteJson(new Dictionary<string, object?>
        {
            ["error"] = message,
        });
    }

    private static void WriteJson(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }
}
