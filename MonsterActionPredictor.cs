using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Random;

namespace MonsterActionPredictor
{
    // v1.0.3 — Forked by xiatinfeng. Replaced Harmony RollMove patch with reflection-based polling
    // to avoid ICombatState JIT TypeLoadException on STS2 SDK 4.6.2.
    [ModInitializerAttribute("Initialize")]
    public class MonsterActionPredictorMod
    {
        private static Harmony _harmony;
        public static Dictionary<Creature, NActionPredictor> Predictors = new Dictionary<Creature, NActionPredictor>();
        public static bool EnableDebugLog = false;

        public static void Log(string message)
        {
            if (EnableDebugLog)
            {
                GD.Print(message);
            }
        }

        public static void Initialize()
        {
            _harmony = new Harmony("MonsterActionPredictor");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            GD.Print("Monster Action Predictor mod initialized!");
        }

        public static int GetBuffAmount(MonsterModel monster)
        {
            if (monster == null) return 0;

            var monsterType = monster.GetType();
            var buffAmtField = monsterType.GetField("_buffAmt", BindingFlags.NonPublic | BindingFlags.Static);
            if (buffAmtField != null)
            {
                var value = buffAmtField.GetValue(null);
                if (value is int intVal)
                {
                    return intVal;
                }
            }
            return 0;
        }

        private static string GetIntentDetails(AbstractIntent intent, Creature owner, int strengthBonus)
        {
            if (intent == null) return "null";

            var type = intent.GetType().Name;
            var intentType = intent.IntentType;
            if (intent is AttackIntent attackIntent)
            {
                var baseDamage = attackIntent.DamageCalc?.Invoke() ?? 0;
                var repeats = attackIntent.Repeats;
                var modifiedBaseDamage = (int)baseDamage + strengthBonus;
                var totalDamage = modifiedBaseDamage * Math.Max(1, repeats);
                return $"{type}(IntentType={intentType}, BaseDamage={(int)baseDamage}, ModifiedBaseDamage={modifiedBaseDamage}, Repeats={repeats}, TotalDamage={totalDamage})";
            }

            if (intent is StatusIntent statusIntent)
            {
                return $"{type}(IntentType={intentType}, CardCount={statusIntent.CardCount})";
            }

            if (intent is DebuffIntent debuffIntent)
            {
                var strongField = typeof(DebuffIntent).GetField("_strong", BindingFlags.NonPublic | BindingFlags.Instance);
                var isStrong = strongField?.GetValue(debuffIntent) as bool? ?? false;
                return $"{type}(IntentType={intentType}, IsStrong={isStrong})";
            }

            if (intent is BuffIntent)
            {
                var buffInfo = GetBuffInfo(owner);
                if (buffInfo != null)
                {
                    return $"{type}(IntentType={intentType}, {buffInfo})";
                }
            }

            return $"{type}(IntentType={intentType})";
        }

        private static string GetBuffInfo(Creature creature)
        {
            var monster = creature.Monster;
            if (monster == null) return null;

            var monsterType = monster.GetType();

            var buffAmtField = monsterType.GetField("_buffAmt", BindingFlags.NonPublic | BindingFlags.Static);
            if (buffAmtField != null)
            {
                var value = buffAmtField.GetValue(null);
                if (value != null)
                {
                    return $"BuffAmt={value}";
                }
            }

            return null;
        }

        [HarmonyPatch(typeof(NCreature), nameof(NCreature._Ready))]
        public class PatchNCreatureReady
        {
            public static void Postfix(NCreature __instance)
            {
                if (__instance.Entity.IsEnemy && __instance.Entity.Monster != null)
                {
                    var predictor = new NActionPredictor();
                    predictor.AssociatedCreature = __instance.Entity;
                    __instance.AddChild(predictor);
                    predictor.Position = new Vector2(80, -130);
                    predictor.Visible = true;
                    Predictors[__instance.Entity] = predictor;
                    Log($"[MonsterActionPredictor] Created predictor for [{__instance.Entity.Name}][{__instance.Entity.SlotName}], total: {Predictors.Count}");
                }
            }
        }
    }

    public partial class NActionPredictor : Control
    {
        private VBoxContainer _verticalContainer;
        private List<HBoxContainer> _rowContainers = new List<HBoxContainer>();
        private SceneTreeTimer _updateTimer;

        public Creature AssociatedCreature { get; set; }

        public override void _Ready()
        {
            ZIndex = 999;
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            SizeFlagsVertical = SizeFlags.ShrinkCenter;

            _verticalContainer = new VBoxContainer();
            _verticalContainer.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            _verticalContainer.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            AddChild(_verticalContainer);
            Visible = true;
        }

        public override void _EnterTree()
        {
            if (CombatManager.Instance?.StateTracker != null)
                CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        }

        public override void _ExitTree()
        {
            if (CombatManager.Instance?.StateTracker != null)
                CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;
            _updateTimer?.Dispose();
        }

        private void OnCombatStateChanged(CombatState _)
        {
            _updateTimer?.Dispose();
            _updateTimer = GetTree().CreateTimer(1.0f);
            _updateTimer.Timeout += () =>
            {
                if (!IsInstanceValid(this) || AssociatedCreature == null) return;

                var monster = AssociatedCreature.Monster;
                if (monster?.MoveStateMachine == null) return;

                List<MoveState> moves = new List<MoveState>();
                try
                {
                    // 1. Check current state exists (don't add to moves — game already shows it)
                    var currentStateField = typeof(MonsterMoveStateMachine).GetField("_currentState",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var currentState = currentStateField?.GetValue(monster.MoveStateMachine) as MoveState;
                    if (currentState == null) return;

                    // 2. Get targets via reflection (CRITICAL: don't access CombatState directly!)
                    IEnumerable<Creature> targets = Array.Empty<Creature>();
                    try
                    {
                        var creatureType = AssociatedCreature.GetType();
                        var csProp = creatureType.GetProperty("CombatState");
                        var combatState = csProp?.GetValue(AssociatedCreature);
                        if (combatState != null)
                        {
                            var pcProp = combatState.GetType().GetProperty("PlayerCreatures");
                            targets = (pcProp?.GetValue(combatState) as IEnumerable<Creature>) ?? Array.Empty<Creature>();
                        }
                    }
                    catch { }

                    // 3. Clone state machine and predict 2 next moves via reflection
                    var clonedMachine = CloneStateMachine(monster.MoveStateMachine);

                    // Get RNG via reflection
                    var runRngProp = typeof(MonsterModel).GetProperty("RunRng");
                    var runRng = runRngProp?.GetValue(monster);
                    var maProp = runRng?.GetType().GetProperty("MonsterAi");
                    var monsterAi = maProp?.GetValue(runRng);
                    var seed = (uint)(monsterAi?.GetType().GetProperty("Seed")?.GetValue(monsterAi) ?? 0u);
                    var counter = (int)(monsterAi?.GetType().GetProperty("Counter")?.GetValue(monsterAi) ?? 0);
                    var rng = new Rng(seed, counter);

                    // Set performed first move
                    var pfmField = typeof(MonsterMoveStateMachine).GetField("_performedFirstMove",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    pfmField?.SetValue(clonedMachine, true);

                    // Call RollMove twice via reflection to predict 2 future moves
                    var rollMoveMethod = typeof(MonsterMoveStateMachine).GetMethod("RollMove");
                    const int PREDICTION_COUNT = 2;
                    for (int i = 0; i < PREDICTION_COUNT; i++)
                    {
                        var predicted = rollMoveMethod?.Invoke(clonedMachine, new object[] { targets, AssociatedCreature, rng }) as MoveState;
                        if (predicted != null) moves.Add(predicted);
                    }
                }
                catch (Exception e)
                {
                    GD.PrintErr("[MonsterActionPredictor] Prediction error: " + e.Message);
                }

                if (moves.Count > 0)
                    UpdateMoves(moves, AssociatedCreature);
            };
        }

        public void UpdateMoves(List<MoveState> moves, Creature creature)
        {
            if (!IsInstanceValid(this)) return;

            try
            {
                IEnumerable<Creature> targets = Array.Empty<Creature>();
                try
                {
                    var creatureType = creature.GetType();
                    var csProp = creatureType.GetProperty("CombatState");
                    var combatState = csProp?.GetValue(creature);
                    if (combatState != null)
                    {
                        var pcProp = combatState.GetType().GetProperty("PlayerCreatures");
                        targets = (pcProp?.GetValue(combatState) as IEnumerable<Creature>) ?? Array.Empty<Creature>();
                    }
                }
                catch { }

                int neededRows = moves?.Count ?? 0;

                // Grow row containers if needed
                while (_rowContainers.Count < neededRows)
                {
                    var row = new HBoxContainer();
                    row.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
                    row.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                    _verticalContainer.AddChild(row);
                    _rowContainers.Add(row);
                }

                // Shrink row containers if needed
                while (_rowContainers.Count > neededRows)
                {
                    var extraRow = _rowContainers[_rowContainers.Count - 1];
                    _verticalContainer.RemoveChild(extraRow);
                    extraRow.QueueFree();
                    _rowContainers.RemoveAt(_rowContainers.Count - 1);
                }

                for (int rowIndex = 0; rowIndex < neededRows; rowIndex++)
                {
                    var move = moves[rowIndex];
                    var row = _rowContainers[rowIndex];
                    var intents = move.Intents ?? new List<AbstractIntent>();

                    // Grow intent nodes if needed
                    while (row.GetChildCount() < intents.Count)
                    {
                        var intentNode = NIntent.Create(0f);
                        if (intentNode == null) continue;
                        intentNode.Scale = new Vector2(0.6f, 0.6f);
                        intentNode.Modulate = new Color(0.7f, 0.7f, 0.8f, 0.5f);
                        row.AddChild(intentNode);
                    }

                    // Shrink intent nodes if needed
                    while (row.GetChildCount() > intents.Count)
                    {
                        var extra = row.GetChild(row.GetChildCount() - 1);
                        row.RemoveChild(extra);
                        extra.QueueFree();
                    }

                    // Update intent display — no strength accumulation, show raw intents
                    for (int intentIndex = 0; intentIndex < intents.Count; intentIndex++)
                    {
                        var intentNode = row.GetChild<NIntent>(intentIndex);
                        if (intentNode != null)
                        {
                            intentNode.UpdateIntent(intents[intentIndex], targets, creature);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                GD.PrintErr("[MonsterActionPredictor] Error in UpdateMoves: " + e.Message);
            }
        }

        private MonsterMoveStateMachine CloneStateMachine(MonsterMoveStateMachine source)
        {
            if (source == null) return null;

            var type = typeof(MonsterMoveStateMachine);

            // Clone States dict
            var statesProp = type.GetProperty("States");
            var sourceStates = statesProp?.GetValue(source) as Dictionary<string, MonsterState>;
            var clonedStates = new Dictionary<string, MonsterState>();
            if (sourceStates != null)
            {
                foreach (var kv in sourceStates)
                {
                    var shallow = typeof(object).GetMethod("MemberwiseClone",
                        BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(kv.Value, null) as MonsterState;
                    if (shallow != null) clonedStates[kv.Key] = shallow;
                }
            }

            // Get initial state
            var initField = type.GetField("_initialState", BindingFlags.NonPublic | BindingFlags.Instance);
            var origInitial = initField?.GetValue(source) as MonsterState;
            var clonedInitial = origInitial != null && clonedStates.TryGetValue(origInitial.Id, out var ci) ? ci : clonedStates.Values.FirstOrDefault();

            // Create clone via reflection
            var ctor = type.GetConstructor(new[] { typeof(IEnumerable<MonsterState>), typeof(MonsterState) });
            var clone = (MonsterMoveStateMachine)ctor.Invoke(new object[] { clonedStates.Values.ToList(), clonedInitial });

            // Copy _currentState
            var curField = type.GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
            var origCurrent = curField?.GetValue(source) as MonsterState;
            if (origCurrent != null && clonedStates.TryGetValue(origCurrent.Id, out var cc))
                curField?.SetValue(clone, cc);

            // Copy _performedFirstMove
            var pfmField = type.GetField("_performedFirstMove", BindingFlags.NonPublic | BindingFlags.Instance);
            pfmField?.SetValue(clone, pfmField?.GetValue(source));

            return clone;
        }

        private bool IsInstanceValid(GodotObject obj) => obj != null && GodotObject.IsInstanceValid(obj);
    }
}
