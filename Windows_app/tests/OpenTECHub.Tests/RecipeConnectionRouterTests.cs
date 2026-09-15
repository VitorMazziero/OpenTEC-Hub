using System.Windows;
using OpenTECHub.Services.Recipes;
using Xunit;

namespace OpenTECHub.Tests;

public sealed class RecipeConnectionRouterTests
{
    [Fact]
    public void Aligned_ports_use_a_single_straight_segment()
    {
        var route = RecipeConnectionRouter.Build(
            new Point(100, 120), new Point(500, 120), "a", "b", []);

        Assert.Equal(2, route.Points.Count);
        Assert.Equal(route.Points[0].Y, route.Points[1].Y);
    }

    [Fact]
    public void Route_uses_another_lane_when_a_block_is_in_the_direct_path()
    {
        var obstacle = new RecipeRouteObstacle("block", new Rect(250, 70, 100, 100));
        var route = RecipeConnectionRouter.Build(
            new Point(100, 120), new Point(500, 120), "a", "b", [obstacle]);

        Assert.True(route.Points.Count >= 4);
        Assert.Contains(route.Points, point => point.Y < obstacle.Bounds.Top || point.Y > obstacle.Bounds.Bottom);

        for (var i = 1; i < route.Points.Count; i++)
        {
            var from = route.Points[i - 1];
            var to = route.Points[i];
            Assert.True(Math.Abs(from.X - to.X) < 0.01 || Math.Abs(from.Y - to.Y) < 0.01);
        }
    }
}
