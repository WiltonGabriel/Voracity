using System.Runtime.InteropServices;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Core.Engine.Systems;

/// <summary>
/// Implements exact EU5 Proximity &amp; Control formulas (Control.md) using QuikGraph's undirected adjacency graph
/// and a pre-allocated reusable Dijkstra buffer (zero per-tick heap allocations).
/// </summary>
public sealed class ProximityAndControlSystem : ITickSystem
{
    private readonly PriorityQueue<int, double> _queue = new();
    private double[] _dist = Array.Empty<double>();
    private bool[] _visited = Array.Empty<bool>();
    private double[] _tribesShare = Array.Empty<double>();
    private double[] _avgSat = Array.Empty<double>();
    private long[] _popTotals = Array.Empty<long>();

    public void Execute(SimulationState state, TickPhase phase)
    {
        if (phase != TickPhase.Initialize && phase != TickPhase.RestoreDerived && phase != TickPhase.Monthly)
            return;

        EnsureBuffers(state.Locations.Length);
        ComputeLocationPopStats(state);

        var snapToCurrent = phase == TickPhase.Initialize;
        var preserveExistingControl = phase == TickPhase.RestoreDerived;

        for (var n = 0; n < state.NationsRuntime.Length; n++)
        {
            if (phase == TickPhase.Initialize || phase == TickPhase.RestoreDerived || state.NationsRuntime[n].ProximityDirty)
            {
                RecomputeNationProximity(state, n);
                state.NationsRuntime[n].ProximityDirty = false;
            }
        }

        for (var i = 0; i < state.HabitableCount; i++)
        {
            var lIdx = state.HabitableLocationOrderByAccess[i];
            ref var loc = ref state.Locations[lIdx];
            loc.MaxControl = ComputeMaxControl(
                loc.Proximity,
                loc.IntegrationStatus,
                loc.Rank,
                _avgSat[lIdx]);

            if (preserveExistingControl)
                continue;

            if (snapToCurrent)
            {
                loc.Control = loc.MaxControl;
            }
            else if (loc.Control < loc.MaxControl)
            {
                loc.Control = Math.Min(loc.MaxControl, loc.Control + 2.0);
            }
            else if (loc.Control > loc.MaxControl)
            {
                loc.Control = Math.Max(loc.MaxControl, loc.Control - 1.0);
            }
        }
    }

    private void EnsureBuffers(int locationCount)
    {
        if (_dist.Length >= locationCount)
            return;
        _dist = new double[locationCount];
        _visited = new bool[locationCount];
        _tribesShare = new double[locationCount];
        _avgSat = new double[locationCount];
        _popTotals = new long[locationCount];
    }

    private void ComputeLocationPopStats(SimulationState state)
    {
        Array.Clear(_tribesShare);
        Array.Clear(_popTotals);
        Array.Fill(_avgSat, 100.0);

        var satSum = _avgSat; // reuse carefully after zeroing
        Array.Clear(satSum);
        var tribesPop = _tribesShare;

        var popSpan = CollectionsMarshal.AsSpan(state.Pops);
        for (var i = 0; i < popSpan.Length; i++)
        {
            ref readonly var p = ref popSpan[i];
            if (!p.Active || p.Population <= 0 || (uint)p.LocationIndex >= (uint)_popTotals.Length)
                continue;

            _popTotals[p.LocationIndex] += p.Population;
            satSum[p.LocationIndex] += p.Population * p.Satisfaction;
            if (p.Class == SocialClass.Tribesmen)
                tribesPop[p.LocationIndex] += p.Population;
        }

        for (var i = 0; i < _popTotals.Length; i++)
        {
            if (_popTotals[i] > 0)
            {
                _avgSat[i] = Math.Clamp(satSum[i] / _popTotals[i], 0.0, 100.0);
                _tribesShare[i] = tribesPop[i] / _popTotals[i];
            }
            else
            {
                _avgSat[i] = 100.0;
                _tribesShare[i] = 0.0;
            }
        }
    }

    private void RecomputeNationProximity(SimulationState state, int nationIdx)
    {
        var capLocIdx = state.NationsRuntime[nationIdx].CapitalLocationIndex;
        if (capLocIdx < 0)
            return;

        var graph = state.Topology.AdjacencyGraph;
        var capFaceId = state.Locations[capLocIdx].PersistentUid;
        if (!graph.ContainsVertex(capFaceId))
            return;

        Array.Fill(_dist, double.PositiveInfinity);
        Array.Clear(_visited);
        _queue.Clear();

        _dist[capLocIdx] = 0.0;
        _queue.Enqueue(capLocIdx, 0.0);

        while (_queue.Count > 0)
        {
            var uIdx = _queue.Dequeue();
            if (_visited[uIdx])
                continue;
            _visited[uIdx] = true;

            var uCost = _dist[uIdx];
            if (uCost >= 100.0)
                continue;

            var uFaceId = state.Locations[uIdx].PersistentUid;
            foreach (var edge in graph.AdjacentEdges(uFaceId))
            {
                var vFaceId = edge.Source == uFaceId ? edge.Target : edge.Source;
                if (!state.LocationIndexByFaceId.TryGetValue(vFaceId, out var vIdx))
                    continue;
                if (_visited[vIdx])
                    continue;

                ref readonly var uLoc = ref state.Locations[uIdx];
                ref readonly var vLoc = ref state.Locations[vIdx];

                if (vLoc.Kind == LocationKind.Impassable)
                    continue;
                if (vLoc.Kind == LocationKind.Habitable && vLoc.OwnerNationIndex != nationIdx)
                    continue;

                var stepCost = ComputeStepProximityCost(
                    in uLoc,
                    in vLoc,
                    edge.Tag,
                    _tribesShare[vIdx]);

                var candidate = uCost + stepCost;
                if (candidate < _dist[vIdx] && candidate <= 150.0)
                {
                    _dist[vIdx] = candidate;
                    _queue.Enqueue(vIdx, candidate);
                }
            }
        }

        for (var i = 0; i < state.Locations.Length; i++)
        {
            if (state.Locations[i].OwnerNationIndex != nationIdx || state.Locations[i].Kind != LocationKind.Habitable)
                continue;
            var cost = _dist[i];
            state.Locations[i].Proximity = double.IsInfinity(cost)
                ? 0.0
                : Math.Clamp(100.0 - cost, 0.0, 100.0);
        }
    }

    public static double ComputeStepProximityCost(
        in LocationRuntime from,
        in LocationRuntime to,
        LocationEdgeInfo edgeInfo,
        double toTribesRatio)
    {
        if (edgeInfo.Kind == LocationEdgeKind.SeaSea || edgeInfo.Kind == LocationEdgeKind.Strait)
        {
            var mp = from.Kind == LocationKind.SeaZone
                ? from.SeaZoneMaritimePresence
                : (to.Kind == LocationKind.SeaZone ? to.SeaZoneMaritimePresence : 0.50);
            mp = Math.Clamp(mp, 0.0, 1.0);
            return mp * 5.0 + (1.0 - mp) * 30.0;
        }

        if (edgeInfo.Kind == LocationEdgeKind.CoastSea)
        {
            var landPort = from.Kind == LocationKind.SeaZone ? to : from;
            var baseCost = 40.0 - (edgeInfo.HasRiver ? 28.0 : 0.0);
            var harborFactor = Math.Max(0.15, 1.0 - 0.50 * (landPort.NaturalHarborSuitability / 100.0));
            return Math.Max(2.0, baseCost * harborFactor);
        }

        // Land <-> Land edge
        var roadReduction = edgeInfo.RoadLevel switch
        {
            RoadLevel.GravelRoad => 20.0,
            RoadLevel.PavedRoad => 25.0,
            RoadLevel.ModernRoad => 30.0,
            RoadLevel.Railroad => 35.0,
            _ => 0.0
        };
        var riverReduction = edgeInfo.HasRiver ? 28.0 : 0.0;
        var hasRoadOrRiver = roadReduction > 0.0 || riverReduction > 0.0;
        var baseLandCost = Math.Max(2.0, 40.0 - Math.Max(roadReduction, riverReduction));

        var localSpeed = GetLocalProximitySpeed(in to, hasRoadOrRiver, toTribesRatio);
        return ApplyProximitySpeedDivisor(baseLandCost, localSpeed);
    }

    public static double GetLocalProximitySpeed(in LocationRuntime loc, bool hasRoadOrRiver, double tribesRatio)
    {
        var speed = loc.Topography switch
        {
            TopographyType.Plateau => -0.125,
            TopographyType.Wetlands => -0.25,
            TopographyType.Hills => -0.25,
            TopographyType.Mountains => -0.50,
            _ => 0.0
        };

        // Per EU5 Control.md: vegetation proximity penalty is ignored when traveling along a road or river
        if (!hasRoadOrRiver)
        {
            speed += loc.Vegetation switch
            {
                VegetationType.Desert => -0.05,
                VegetationType.Farmland => -0.05,
                VegetationType.Woods => -0.125,
                VegetationType.Forest => -0.25,
                VegetationType.Jungle => -0.50,
                _ => 0.0
            };
        }

        speed += 0.50 * (Math.Clamp(loc.Development, 0.0, 100.0) / 100.0);
        speed -= Math.Clamp(tribesRatio, 0.0, 1.0);
        return speed;
    }

    /// <summary>
    /// Exact EU5 Control.md divisor rule: positive speed gives diminishing returns (/ (1 + speed));
    /// negative speed scales exponentially (/ (1 - |speed|)).
    /// </summary>
    public static double ApplyProximitySpeedDivisor(double baseCost, double totalSpeed)
    {
        var divisor = Math.Max(0.10, 1.0 + totalSpeed);
        return baseCost / divisor;
    }

    public static double ComputeMaxControl(
        double proximity,
        IntegrationStatus integration,
        LocationRank rank,
        double avgSatisfaction)
    {
        var maxCtrl = 0.75 * Math.Clamp(proximity, 0.0, 100.0);
        maxCtrl += integration switch
        {
            IntegrationStatus.Core => 20.0,
            IntegrationStatus.Integrated => 5.0,
            IntegrationStatus.Conquered => -10.0,
            _ => -10.0
        };
        maxCtrl += rank switch
        {
            LocationRank.Town => 5.0,
            LocationRank.City => 10.0,
            LocationRank.Megalopolis => 20.0,
            _ => 0.0
        };
        var satPenalty = -10.0 * (1.0 - Math.Clamp(avgSatisfaction, 0.0, 100.0) / 100.0);
        return Math.Clamp(maxCtrl + satPenalty, 0.0, 100.0);
    }
}
