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
        
        private static int _pendingPredictions;
        private static int _rngCounterBeforeRollMoves;
        private static List<Creature> _creaturesToPredict = new List<Creature>();

        public static void Initialize()
        {
            _harmony = new Harmony("MonsterActionPredictor");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
            GD.Print("Monster Action Predictor mod initialized!");
        }

        public static void UpdatePrediction(Creature creature, int rngCounter)
        {
            if (Predictors.TryGetValue(creature, out var predictor))
            {
                var moves = PredictMoves(creature.Monster, PREDICTION_COUNT, rngCounter);
                predictor.UpdateMoves(moves, creature);
            }
        }

        private static List<MoveState> PredictMoves(MonsterModel monster, int count, int rngCounter)
        {
            var predictions = new List<MoveState>();
            try
            {
                if (monster.MoveStateMachine == null) return predictions;

                var currentStateField = typeof(MonsterMoveStateMachine).GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Instance);
                var currentState = (MonsterState)currentStateField.GetValue(monster.MoveStateMachine);
                if (currentState?.Id == "STUNNED") return predictions;

                var clonedMachine = CloneStateMachine(monster.MoveStateMachine);
                var targets = monster.CombatState.PlayerCreatures;
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
                var targets = creature.CombatState?.PlayerCreatures;
                if (targets == null) return;

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
                            intentNode.Modulate = new Color(0.7f, 0.7f, 0.8f, 0.6f);
                            row.AddChild(intentNode);
                        }

                        intentNode.UpdateIntent(intents[intentIndex], targets, creature);
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
    }
}
