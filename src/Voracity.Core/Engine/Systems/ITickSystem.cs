namespace Voracity.Core.Engine.Systems;

public enum TickPhase
{
    Initialize,
    RestoreDerived,
    Daily,
    Weekly,
    Monthly
}

public interface ITickSystem
{
    void Execute(SimulationState state, TickPhase phase);
}

public sealed class TickPipeline
{
    private readonly ITickSystem[] _systems;

    public TickPipeline(params ITickSystem[] systems)
    {
        _systems = systems;
    }

    public void InitializeAll(SimulationState state)
    {
        for (var i = 0; i < _systems.Length; i++)
            _systems[i].Execute(state, TickPhase.Initialize);
    }

    public void RestoreDerivedAll(SimulationState state)
    {
        for (var i = 0; i < _systems.Length; i++)
            _systems[i].Execute(state, TickPhase.RestoreDerived);
    }

    public void ExecuteSubTick(SimulationState state)
    {
        state.Clock.AdvanceSubTick();
        if (!state.Clock.IsNewDay)
            return;

        RunPhase(state, TickPhase.Daily);

        if (state.Clock.IsWeeklyTick)
            RunPhase(state, TickPhase.Weekly);

        if (state.Clock.IsMonthlyTick)
            RunPhase(state, TickPhase.Monthly);
    }

    private void RunPhase(SimulationState state, TickPhase phase)
    {
        for (var i = 0; i < _systems.Length; i++)
            _systems[i].Execute(state, phase);
    }
}
