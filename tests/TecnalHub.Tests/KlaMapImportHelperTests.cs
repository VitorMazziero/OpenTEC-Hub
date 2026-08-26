using System;
using System.Collections.Generic;
using TecnalHub.Services.KlaMapping;
using TecnalHub.Services.KlaTesting;
using Xunit;

namespace TecnalHub.Tests;

public sealed class KlaMapImportHelperTests
{
    [Fact]
    public void ImportConditionsFromMap_Extracts_Distinct_NQ_Snapshots_Correctly()
    {
        var mapDoc = new KlaExperimentDocument
        {
            Snapshot = new KlaExperimentSnapshot
            {
                Id = Guid.NewGuid(),
                Name = "Biorreator 5L Testes Iniciais",
                Anchors =
                [
                    new KlaAnchor(2.0, 300, 45.0),
                    new KlaAnchor(2.0, 300, 47.0), // Duplicate (N, Q) in anchor list
                    new KlaAnchor(4.0, 500, 80.0),
                    new KlaAnchor(6.0, 700, 120.0),
                ],
            },
        };

        var (mapRef, conditions) = KlaMapImportHelper.ImportConditionsFromMap(mapDoc, defaultReplicates: 3);

        Assert.NotNull(mapRef);
        Assert.Equal(mapDoc.Snapshot.Id, mapRef.MapId);
        Assert.Equal("Biorreator 5L Testes Iniciais", mapRef.MapName);

        Assert.Equal(3, conditions.Count);

        // Ordered by N then Q
        Assert.Equal(300, conditions[0].AgitationRpm);
        Assert.Equal(2.0, conditions[0].AirflowLpm);
        Assert.Equal(3, conditions[0].RequestedReplicates);
        Assert.Equal(ConditionOrigin.Map, conditions[0].Origin);
        Assert.Equal(ConditionStatus.Pending, conditions[0].Status);

        Assert.Equal(500, conditions[1].AgitationRpm);
        Assert.Equal(4.0, conditions[1].AirflowLpm);

        Assert.Equal(700, conditions[2].AgitationRpm);
        Assert.Equal(6.0, conditions[2].AirflowLpm);
    }
}
