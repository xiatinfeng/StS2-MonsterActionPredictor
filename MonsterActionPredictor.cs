using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Random;

namespace MonsterActionPredictor
{
    [ModInitializerAttribute("Initialize")]
    public class MonsterActionPredictorMod
    {
        private static Harmony _harmony;
        public static Dictionary<Creature, NActionPredictor> Predictors = new Dictionary<Creature, NActionPredictor>();
        private static readonly int PREDICTION_COUNT = 2;
        public static bool EnableDebugLog = true;
        
        private static int _pendingPredictions;
        private static int _rngCounterBeforeRollMoves;
        private static List<Creature> _creaturesToPredict = new List<Creature>();

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

        public static void UpdatePrediction(Creature creature, int rngCounter)
        {
            if (creature?.Monster == null)
            {
                return;
            }

            if (Predictors.TryGetValue(creature, out var predictor))
            {
                var moves = PredictMoves(creature.Monster, PREDICTION_COUNT, rngCounter);
                predictor.UpdateMoves(moves, creature);
            }
        }

        private static void LogAllPredictions(int rngCounter)
        {
            Log($"[MonsterActionPredictor] ========== TURN PREDICTIONS (rngCounter: {rngCounter}) ==========");
            foreach (var kvp in Predictors)
            {
                var creature = kvp.Key;
                var monster = creature.Monster;
                if (monster?.MoveStateMachine == null) continue;

                var currentStateField = typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
                var currentState = currentStateField?.GetValue(monster.MoveStateMachine) as MonsterState;

                Log($"[{creature.Name}][{creature.SlotName}] === CURRENT TURN ===");
                if (currentState is MoveState currentMove)
                {
                    Log($"[{creature.Name}][{creature.SlotName}]   Move: {currentMove.Id}");
                    if (currentMove.Intents != null)
                    {
                        foreach (var intent in currentMove.Intents)
                        {
                            Log($"[{creature.Name}][{creature.SlotName}]     {GetIntentDetails(intent, creature, 0)}");
                        }
                    }
                }

                var moves = PredictMoves(monster, PREDICTION_COUNT, rngCounter);
                Log($"[{creature.Name}][{creature.SlotName}] === FUTURE ({moves.Count} turns) ===");
                
                int accumulatedStrength = GetBuffAmount(monster);
                for (int i = 0; i < moves.Count; i++)
                {
                    var move = moves[i];
                    Log($"[{creature.Name}][{creature.SlotName}]   Turn+{i + 1}: {move?.Id ?? "null"} (Strength: +{accumulatedStrength})");
                    if (move?.Intents != null)
                    {
                        foreach (var intent in move.Intents)
                        {
                            Log($"[{creature.Name}][{creature.SlotName}]     {GetIntentDetails(intent, creature, accumulatedStrength)}");
                            if (intent is BuffIntent)
                            {
                                accumulatedStrength += GetBuffAmount(monster);
                            }
                        }
                    }
                }
            }
            Log($"[MonsterActionPredictor] ========== END PREDICTIONS ==========");
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

        private static List<MoveState> PredictMoves(MonsterModel monster, int count, int rngCounter)
        {
            var predictions = new List<MoveState>();
            if (monster == null)
            {
                return predictions;
            }

            try
            {
                if (monster.MoveStateMachine == null) return predictions;

                var currentStateField = typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
                var currentState = (MonsterState)currentStateField.GetValue(monster.MoveStateMachine);
                if (currentState?.Id == "STUNNED") return predictions;

                var combatState = monster.Creature.CombatState;
                if (combatState == null)
                {
                    return predictions;
                }

                var clonedMachine = CloneStateMachine(monster.MoveStateMachine);
                var targets = combatState.PlayerCreatures;
                var rng = new Rng(monster.RunRng.MonsterAi.Seed, rngCounter);

                SetPerformedFirstMove(clonedMachine, true);

                for (int i = 0; i < count; i++)
                {
                    var nextMove = clonedMachine.RollMove(targets, monster.Creature, rng);
                    predictions.Add(nextMove);
                    
                    if (nextMove.MustPerformOnceBeforeTransitioning)
                    {
                        var performedAtLeastOnceField = typeof(MoveState).GetField("_performedAtLeastOnce", BindingFlags.NonPublic | BindingFlags.Instance);
                        performedAtLeastOnceField?.SetValue(nextMove, true);
                    }
                }
            }
            catch (Exception e)
            {
                GD.PrintErr("[MonsterActionPredictor] Error predicting moves: " + e.Message);
            }
            return predictions;
        }

        private static void SetPerformedFirstMove(MonsterMoveStateMachine machine, bool value)
        {
            var performedFirstMoveField = typeof(MonsterMoveStateMachine).GetField("_performedFirstMove", BindingFlags.NonPublic | BindingFlags.Instance);
            performedFirstMoveField?.SetValue(machine, value);
        }

        private static T ShallowClone<T>(T original)
        {
            var method = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
            return (T)method.Invoke(original, null);
        }

        private static MonsterMoveStateMachine CloneStateMachine(MonsterMoveStateMachine original)
        {
            var clonedStatesList = new List<MonsterState>();
            
            foreach (var originalState in original.States.Values)
            {
                var clonedState = ShallowClone(originalState);
                clonedStatesList.Add(clonedState);
            }

            var initialStateField = typeof(MonsterMoveStateMachine).GetField("_initialState", BindingFlags.NonPublic | BindingFlags.Instance);
            var originalInitialState = (MonsterState)initialStateField.GetValue(original);
            MonsterState clonedInitialState = clonedStatesList.First(s => s.Id == originalInitialState.Id);

            var ctor = typeof(MonsterMoveStateMachine).GetConstructor(new[] { typeof(IEnumerable<MonsterState>), typeof(MonsterState) });
            var clone = (MonsterMoveStateMachine)ctor.Invoke(new object[] { clonedStatesList, clonedInitialState });

            var currentStateField = typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
            var originalCurrentState = (MonsterState)currentStateField.GetValue(original);
            MonsterState clonedCurrentState = clone.States[originalCurrentState.Id];
            currentStateField.SetValue(clone, clonedCurrentState);

            var performedFirstMoveField = typeof(MonsterMoveStateMachine).GetField("_performedFirstMove", BindingFlags.NonPublic | BindingFlags.Instance);
            var performedFirstMove = (bool)performedFirstMoveField.GetValue(original);
            performedFirstMoveField.SetValue(clone, performedFirstMove);

            foreach (var state in clone.States.Values)
            {
                if (state is MoveState moveState && moveState.FollowUpStateId != null)
                {
                    if (clone.States.TryGetValue(moveState.FollowUpStateId, out var followUpState))
                    {
                        var followUpStateProperty = typeof(MoveState).GetProperty("FollowUpState");
                        followUpStateProperty.SetValue(moveState, followUpState);
                    }
                }
            }

            return clone;
        }

        private static void StartRollMovePhase(MonsterModel monster)
        {
            if (_pendingPredictions == 0)
            {
                _rngCounterBeforeRollMoves = monster.RunRng.MonsterAi.Counter;
                _creaturesToPredict.Clear();
            }
            _pendingPredictions++;
        }

        private static void EndRollMovePhase(MonsterModel monster)
        {
            _creaturesToPredict.Add(monster.Creature);
            _pendingPredictions--;

            if (_pendingPredictions == 0)
            {
                var finalRngCounter = monster.RunRng.MonsterAi.Counter;
                LogAllPredictions(finalRngCounter);
                foreach (var creature in _creaturesToPredict)
                {
                    UpdatePrediction(creature, finalRngCounter);
                }
                _creaturesToPredict.Clear();
            }
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

        [HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.RollMove))]
        public class PatchMonsterRollMove
        {
            public static void Prefix(MonsterModel __instance)
            {
                StartRollMovePhase(__instance);
            }

            public static void Postfix(MonsterModel __instance)
            {
                EndRollMovePhase(__instance);
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
                if (IsInstanceValid(this) && AssociatedCreature != null)
                {
                    var rngCounter = AssociatedCreature.Monster?.RunRng?.MonsterAi?.Counter ?? 0;
                    MonsterActionPredictorMod.UpdatePrediction(AssociatedCreature, rngCounter);
                }
            };
        }

        public void UpdateMoves(List<MoveState> moves, Creature creature)
        {
            if (!IsInstanceValid(this)) return;

            try
            {
                var targets = creature.CombatState?.PlayerCreatures ?? Array.Empty<Creature>();
                int accumulatedStrength = GetInitialStrengthBonus(creature);

                int rowIndex = 0;
                for (; rowIndex < moves.Count; rowIndex++)
                {
                    HBoxContainer row;
                    if (rowIndex < _rowContainers.Count)
                        row = _rowContainers[rowIndex];
                    else
                    {
                        row = new HBoxContainer();
                        row.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
                        row.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                        _verticalContainer.AddChild(row);
                        _rowContainers.Add(row);
                    }

                    var move = moves[rowIndex];
                    var intents = move.Intents ?? new List<AbstractIntent>();

                    int intentIndex = 0;
                    for (; intentIndex < intents.Count; intentIndex++)
                    {
                        NIntent intentNode;
                        if (intentIndex < row.GetChildCount())
                            intentNode = row.GetChild<NIntent>(intentIndex);
                        else
                        {
                            intentNode = NIntent.Create(0f);
                            if (intentNode == null) continue;
                            intentNode.Modulate = new Color(0.7f, 0.7f, 0.8f, 0.5f);
                            row.AddChild(intentNode);
                        }

                        intentNode.Scale = new Vector2(0.6f, 0.6f);

                        var originalIntent = intents[intentIndex];
                        AbstractIntent displayIntent = originalIntent;
                        
                        if (accumulatedStrength > 0)
                        {
                            if (originalIntent is SingleAttackIntent singleAttackIntent)
                            {
                                displayIntent = new ModifiedSingleAttackIntent(singleAttackIntent, accumulatedStrength);
                            }
                            else if (originalIntent is MultiAttackIntent multiAttackIntent)
                            {
                                displayIntent = new ModifiedMultiAttackIntent(multiAttackIntent, accumulatedStrength);
                            }
                        }

                        intentNode.UpdateIntent(displayIntent, targets, creature);

                        if (originalIntent is BuffIntent)
                        {
                            accumulatedStrength += MonsterActionPredictorMod.GetBuffAmount(creature.Monster);
                        }
                    }

                    while (row.GetChildCount() > intents.Count)
                    {
                        var extra = row.GetChild(row.GetChildCount() - 1);
                        row.RemoveChild(extra);
                        extra.QueueFree();
                    }
                }

                while (_verticalContainer.GetChildCount() > moves.Count)
                {
                    var extraRow = _verticalContainer.GetChild(_verticalContainer.GetChildCount() - 1);
                    _verticalContainer.RemoveChild(extraRow);
                    extraRow.QueueFree();
                    _rowContainers.RemoveAt(_rowContainers.Count - 1);
                }
            }
            catch (Exception e)
            {
                GD.PrintErr("[MonsterActionPredictor] Error in UpdateMoves: " + e.Message);
            }
        }

        private bool IsInstanceValid(GodotObject obj) => obj != null && GodotObject.IsInstanceValid(obj);

        private int GetInitialStrengthBonus(Creature creature)
        {
            var monster = creature.Monster;
            if (monster?.MoveStateMachine == null) return 0;

            var currentStateField = typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
            var currentState = currentStateField?.GetValue(monster.MoveStateMachine) as MonsterState;

            if (currentState is MoveState currentMove && currentMove.Intents != null)
            {
                foreach (var intent in currentMove.Intents)
                {
                    if (intent is BuffIntent)
                    {
                        var buffAmount = MonsterActionPredictorMod.GetBuffAmount(monster);
                        var currentStrength = creature.GetPowerAmount<StrengthPower>();
                        
                        if (currentStrength > 0)
                        {
                            return 0;
                        }
                        return buffAmount;
                    }
                }
            }
            return 0;
        }
    }

    public class ModifiedSingleAttackIntent : SingleAttackIntent
    {
        private readonly SingleAttackIntent _original;
        private readonly int _strengthBonus;

        public ModifiedSingleAttackIntent(SingleAttackIntent original, int strengthBonus)
            : base((int)(original.DamageCalc?.Invoke() ?? 0) + strengthBonus)
        {
            _original = original;
            _strengthBonus = strengthBonus;
        }

        public override int GetTotalDamage(IEnumerable<Creature> targets, Creature owner)
        {
            var baseDamage = _original.DamageCalc?.Invoke() ?? 0;
            var modifiedDamage = (int)baseDamage + _strengthBonus;
            return modifiedDamage;
        }
    }

    public class ModifiedMultiAttackIntent : MultiAttackIntent
    {
        private readonly MultiAttackIntent _original;
        private readonly int _strengthBonus;

        public ModifiedMultiAttackIntent(MultiAttackIntent original, int strengthBonus)
            : base((int)(original.DamageCalc?.Invoke() ?? 0) + strengthBonus, original.Repeats)
        {
            _original = original;
            _strengthBonus = strengthBonus;
        }

        public override int Repeats => _original.Repeats;

        public override int GetTotalDamage(IEnumerable<Creature> targets, Creature owner)
        {
            var baseDamage = _original.DamageCalc?.Invoke() ?? 0;
            var modifiedDamage = (int)baseDamage + _strengthBonus;
            return modifiedDamage * Math.Max(1, Repeats);
        }
    }
}
