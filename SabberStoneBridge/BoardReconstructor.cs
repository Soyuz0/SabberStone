using System.Text.Json;
using System.Text.Json.Serialization;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;

namespace SabberStoneBridge;

public sealed class BoardReconstructor
{
    private const string FillerDeckCardId = "CS2_231"; // Wisp

    public Game Reconstruct(BoardState state)
    {
        if (state.Player is null || state.Opponent is null)
        {
            throw new InvalidOperationException("Board payload must contain both player and opponent states.");
        }

        var game = new Game(new GameConfig
        {
            StartPlayer = IsOpponentCurrent(state.CurrentPlayer) ? 2 : 1,
            Player1Name = "BridgePlayer",
            Player2Name = "BridgeOpponent",
            Player1HeroClass = ParseCardClass(state.Player.HeroClass),
            Player2HeroClass = ParseCardClass(state.Opponent.HeroClass),
            FillDecks = false,
            Shuffle = false,
            SkipMulligan = true,
            Logging = false,
            History = false,
        });

        game.StartGame(stopBeforeShuffling: true);
        game.State = State.RUNNING;
        game.Step = Step.MAIN_ACTION;
        game.Turn = Math.Max(1, state.Turn);

        game.CurrentPlayer = IsOpponentCurrent(state.CurrentPlayer) ? game.Player2 : game.Player1;

        ApplyPlayerState(game.Player1, state.Player, game.CurrentPlayer == game.Player1);
        ApplyPlayerState(game.Player2, state.Opponent, game.CurrentPlayer == game.Player2);

        return game;
    }

    private static void ApplyPlayerState(Controller controller, BridgePlayerState state, bool isCurrentPlayer)
    {
        controller.BaseMana = Math.Max(0, state.ManaTotal);
        controller.UsedMana = Math.Max(0, state.ManaTotal - state.ManaCurrent);
        controller.OverloadLocked = Math.Max(0, state.OverloadLocked);
        controller.OverloadOwed = Math.Max(0, state.OverloadOwed);

        ApplyHeroState(controller, state, isCurrentPlayer);
        AddHand(controller, state.Hand);
        AddBoard(controller, state.Board);
        AddSecrets(controller, state.Secrets);
        AddWeapon(controller, state.Weapon);
        AddDeckCards(controller, Math.Max(0, state.DeckRemaining));
    }

    private static void ApplyHeroState(Controller controller, BridgePlayerState state, bool isCurrentPlayer)
    {
        var hero = controller.Hero;
        hero.Armor = Math.Max(0, state.HeroArmor);

        var requestedHp = Math.Max(0, state.HeroHp);
        if (requestedHp > hero.BaseHealth)
        {
            hero.BaseHealth = requestedHp;
        }
        hero.Damage = Math.Max(0, hero.BaseHealth - requestedHp);

        hero.HeroPower.IsExhausted = state.HeroPowerUsed;
        hero.IsExhausted = false;
        hero.NumAttacksThisTurn = 0;

        // SabberStone hero attack includes weapon damage only for current player.
        // We only apply explicit hero attack when this side is on turn.
        if (isCurrentPlayer)
        {
            hero.AttackDamage = Math.Max(0, state.HeroAtk);
        }
    }

    private static void AddHand(Controller controller, List<BridgeHandCardState> cards)
    {
        foreach (var handCard in cards)
        {
            var card = ResolveCard(handCard.CardId, "hand");
            var playable = Entity.FromCard(controller, card);
            controller.HandZone.Add(playable);
            playable.Cost = Math.Max(0, handCard.Cost);
        }
    }

    private static void AddBoard(Controller controller, List<BridgeBoardMinionState> minions)
    {
        foreach (var minionState in minions.OrderBy(m => m.ZonePos))
        {
            var card = ResolveCard(minionState.CardId, "board");
            var playable = Entity.FromCard(controller, card);
            if (playable is not Minion minion)
            {
                throw new InvalidOperationException($"Board entity '{minionState.CardId}' is not a minion.");
            }

            var desiredPos = Math.Clamp(minionState.ZonePos, 0, controller.BoardZone.Count);
            controller.BoardZone.Add(minion, desiredPos);
            ApplyMinionState(minion, minionState);
        }
    }

    private static void ApplyMinionState(Minion minion, BridgeBoardMinionState state)
    {
        var maxHp = state.MaxHp > 0 ? state.MaxHp : Math.Max(1, state.Hp);

        minion.AttackDamage = Math.Max(0, state.Atk);
        minion.BaseHealth = Math.Max(1, maxHp);
        minion.Damage = Math.Max(0, minion.BaseHealth - Math.Max(0, state.Hp));

        minion.HasTaunt = state.Taunt;
        minion.HasDivineShield = state.DivineShield;
        minion.HasStealth = state.Stealth;
        minion.IsFrozen = state.Frozen;
        minion.Poisonous = state.Poisonous;
        minion.HasWindfury = state.Windfury;

        // Input state does not include exact attack exhaustion/summoning sickness details.
        // Keep minions actionable for search quality.
        minion.IsExhausted = false;
        minion.NumAttacksThisTurn = 0;
    }

    private static void AddSecrets(Controller controller, JsonElement[] secrets)
    {
        foreach (var secretId in ExtractSecretIds(secrets))
        {
            var card = ResolveCard(secretId, "secrets");
            var playable = Entity.FromCard(controller, card);
            if (playable is Spell spell)
            {
                controller.SecretZone.Add(spell);
            }
        }
    }

    private static IEnumerable<string> ExtractSecretIds(JsonElement[] secrets)
    {
        foreach (var secret in secrets)
        {
            if (secret.ValueKind == JsonValueKind.String)
            {
                var value = secret.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
                continue;
            }

            if (secret.ValueKind == JsonValueKind.Object && secret.TryGetProperty("card_id", out var cardIdProp))
            {
                var value = cardIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static void AddWeapon(Controller controller, BridgeWeaponState? weaponState)
    {
        if (weaponState is null)
        {
            return;
        }

        var card = ResolveCard(weaponState.CardId, "weapon");
        var playable = Entity.FromCard(controller, card);
        if (playable is not Weapon weapon)
        {
            throw new InvalidOperationException($"Weapon entity '{weaponState.CardId}' is not a weapon.");
        }

        weapon.AttackDamage = Math.Max(0, weaponState.Attack);
        weapon.Durability = Math.Max(0, weaponState.Durability);
        controller.Hero.AddWeapon(weapon);
    }

    private static void AddDeckCards(Controller controller, int deckCount)
    {
        if (deckCount <= 0)
        {
            return;
        }

        var fillerCard = ResolveCard(FillerDeckCardId, "deck filler");
        for (var i = 0; i < deckCount; i++)
        {
            Entity.FromCard(controller, fillerCard, zone: controller.DeckZone);
        }
    }

    private static Card ResolveCard(string cardId, string context)
    {
        if (string.IsNullOrWhiteSpace(cardId))
        {
            throw new InvalidOperationException($"Missing card id in {context} state.");
        }

        try
        {
            return Cards.FromId(cardId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unknown card id '{cardId}' in {context} state.", ex);
        }
    }

    private static CardClass ParseCardClass(string heroClass)
    {
        if (string.IsNullOrWhiteSpace(heroClass))
        {
            throw new InvalidOperationException("Hero class is required.");
        }

        if (Enum.TryParse<CardClass>(heroClass, ignoreCase: true, out var cardClass))
        {
            return cardClass;
        }

        throw new InvalidOperationException($"Unsupported hero class '{heroClass}'.");
    }

    private static bool IsOpponentCurrent(string? currentPlayer)
    {
        return string.Equals(currentPlayer, "opponent", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BoardState
{
    [JsonPropertyName("player")]
    public BridgePlayerState Player { get; set; } = new();

    [JsonPropertyName("opponent")]
    public BridgePlayerState Opponent { get; set; } = new();

    [JsonPropertyName("turn")]
    public int Turn { get; set; }

    [JsonPropertyName("current_player")]
    public string CurrentPlayer { get; set; } = "player";
}

public sealed class BridgePlayerState
{
    [JsonPropertyName("hero_class")]
    public string HeroClass { get; set; } = "MAGE";

    [JsonPropertyName("hero_hp")]
    public int HeroHp { get; set; }

    [JsonPropertyName("hero_armor")]
    public int HeroArmor { get; set; }

    [JsonPropertyName("hero_atk")]
    public int HeroAtk { get; set; }

    [JsonPropertyName("mana_current")]
    public int ManaCurrent { get; set; }

    [JsonPropertyName("mana_total")]
    public int ManaTotal { get; set; }

    [JsonPropertyName("overload_locked")]
    public int OverloadLocked { get; set; }

    [JsonPropertyName("overload_owed")]
    public int OverloadOwed { get; set; }

    [JsonPropertyName("hand")]
    public List<BridgeHandCardState> Hand { get; set; } = new();

    [JsonPropertyName("board")]
    public List<BridgeBoardMinionState> Board { get; set; } = new();

    [JsonPropertyName("secrets")]
    public JsonElement[] Secrets { get; set; } = [];

    [JsonPropertyName("weapon")]
    public BridgeWeaponState? Weapon { get; set; }

    [JsonPropertyName("hero_power_used")]
    public bool HeroPowerUsed { get; set; }

    [JsonPropertyName("deck_remaining")]
    public int DeckRemaining { get; set; }
}

public sealed class BridgeHandCardState
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = "";

    [JsonPropertyName("cost")]
    public int Cost { get; set; }
}

public sealed class BridgeBoardMinionState
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = "";

    [JsonPropertyName("atk")]
    public int Atk { get; set; }

    [JsonPropertyName("hp")]
    public int Hp { get; set; }

    [JsonPropertyName("max_hp")]
    public int MaxHp { get; set; }

    [JsonPropertyName("taunt")]
    public bool Taunt { get; set; }

    [JsonPropertyName("divine_shield")]
    public bool DivineShield { get; set; }

    [JsonPropertyName("stealth")]
    public bool Stealth { get; set; }

    [JsonPropertyName("frozen")]
    public bool Frozen { get; set; }

    [JsonPropertyName("poisonous")]
    public bool Poisonous { get; set; }

    [JsonPropertyName("windfury")]
    public bool Windfury { get; set; }

    [JsonPropertyName("zone_pos")]
    public int ZonePos { get; set; }
}

public sealed class BridgeWeaponState
{
    [JsonPropertyName("card_id")]
    public string CardId { get; set; } = "";

    [JsonPropertyName("attack")]
    public int Attack { get; set; }

    [JsonPropertyName("durability")]
    public int Durability { get; set; }
}
