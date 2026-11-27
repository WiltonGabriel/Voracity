using Voracity.Core.Models;

namespace Voracity.Core.Engine;

public sealed class SimulationClock
{
    public const int SubTicksPerDay = 4;

    public GameDate CurrentDate { get; set; } = new(1380, 1, 1, 0);
    public bool IsPaused { get; set; } = true;
    public int Speed { get; set; } = 2;
    public int TotalTicksExecuted { get; set; }

    public void AdvanceSubTick()
    {
        TotalTicksExecuted++;
        CurrentDate = CurrentDate.AdvanceOneSubTick();
    }

    public bool IsNewDay => CurrentDate.SubTick6h == 0;
    public bool IsWeeklyTick => IsNewDay && CurrentDate.Day % 7 == 0;
    public bool IsMonthlyTick => IsNewDay && CurrentDate.Day == 1;
}
