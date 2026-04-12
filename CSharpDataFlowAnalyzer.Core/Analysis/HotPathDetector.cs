using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpDataFlowAnalyzer.Analysis;

/// <summary>
/// Identifies the most-connected classes in the dependency graph.
/// These "hot nodes" are the core abstractions newcomers should learn first.
/// </summary>
internal sealed class HotPathDetector
{
    public static List<HotNode> Detect(
        List<ClassRelationship> relationships,
        int topN = 10,
        Action<string>? log = null)
    {
        if (relationships.Count == 0)
            return new List<HotNode>();

        // Calculate median fan-in and fan-out for role classification
        var sortedFanIn = relationships
            .Select(r => r.FanIn)
            .OrderBy(x => x)
            .ToList();
        var sortedFanOut = relationships
            .Select(r => r.FanOut)
            .OrderBy(x => x)
            .ToList();

        int medianFanIn = sortedFanIn[sortedFanIn.Count / 2];
        int medianFanOut = sortedFanOut[sortedFanOut.Count / 2];

        // Rank by total connections descending.
        // Interfaces and abstract classes are "key abstractions" that appear at the
        // top of every fan-in chart, but concrete classes are more actionable for
        // a new developer.  We demote interfaces by halving their effective score
        // so concrete hot classes surface first, while interfaces are still present.
        var ranked = relationships
            .Select(r => new
            {
                Relationship = r,
                TotalConnections = r.FanIn + r.FanOut,
                // Interfaces get a fractional weight so they rank below concrete peers
                EffectiveScore = r.Kind == "interface"
                    ? (r.FanIn + r.FanOut) * 0.5
                    : (double)(r.FanIn + r.FanOut)
            })
            .OrderByDescending(x => x.EffectiveScore)
            .ThenByDescending(x => x.Relationship.FanIn) // break ties by fan-in
            .Take(topN)
            .Select((x, idx) => new HotNode
            {
                ClassId = x.Relationship.ClassId,
                ClassName = x.Relationship.ClassName,
                FanIn = x.Relationship.FanIn,
                FanOut = x.Relationship.FanOut,
                TotalConnections = x.TotalConnections,
                Rank = idx + 1,
                Role = ClassifyRole(x.Relationship.FanIn, x.Relationship.FanOut,
                                     medianFanIn, medianFanOut)
            })
            .ToList();

        log?.Invoke($"  Hot nodes: top {ranked.Count} of {relationships.Count} classes " +
                     $"(median fan-in={medianFanIn}, fan-out={medianFanOut})");

        return ranked;
    }

    private static string ClassifyRole(int fanIn, int fanOut, int medianFanIn, int medianFanOut)
    {
        bool highIn = fanIn > medianFanIn;
        bool highOut = fanOut > medianFanOut;

        return (highIn, highOut) switch
        {
            (true, true)   => "hub",       // highly connected both ways — core abstraction
            (false, true)  => "provider",   // pushes data/calls out, few depend on it
            (true, false)  => "consumer",   // many depend on it, it depends on few
            (false, false) => "leaf"         // peripheral, low connectivity
        };
    }
}
