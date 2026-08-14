namespace Phenome.Geometry.Tests;

/// <summary>Covers the two outline-shaping operations: filleting corners and offsetting sideways.</summary>
public class PolylineShapingTests
{
    private static Polyline ClosedSquare(double size) => PolylineOps.Create(
    [
        PointOps.Create(0, 0, 0),
        PointOps.Create(size, 0, 0),
        PointOps.Create(size, size, 0),
        PointOps.Create(0, size, 0),
        PointOps.Create(0, 0, 0),
    ]);

    [Fact]
    public void Fillet_RoundsEveryCornerOfAClosedSquare()
    {
        OperationResult result = PolylineOps.Fillet(
            ClosedSquare(10), radius: 2, Plane.WorldXY, segmentsPerCorner: 4, out Polyline? filleted);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(PolylineOps.IsClosed(filleted!));

        // Four arcs of five points each, plus the repeated closing point.
        Assert.Equal((4 * 5) + 1, filleted!.PointCount);
    }

    [Fact]
    public void Fillet_TakesTheRightAmountOfAreaOffASquare()
    {
        // A right-angled corner rounded to radius r loses r squared less a quarter circle of it, so all four
        // corners of a square lose exactly what one full circle's bounding square would.
        double radius = 2;
        Polyline square = ClosedSquare(10);

        Assert.True(PolylineOps.Fillet(
            square, radius, Plane.WorldXY, 64, out Polyline? filleted).IsSuccess);

        double lost = (4 * radius * radius) - (Math.PI * radius * radius);
        double expected = 100 - lost;

        Assert.Equal(expected, Math.Abs(PolylineOps.SignedArea(filleted!, Plane.WorldXY)), 1);
    }

    [Fact]
    public void Fillet_StaysInsideTheOriginalOutline()
    {
        // The property that makes it safe to extrude straight afterwards: replacing corners cannot push the
        // outline outwards, so no new crossings appear.
        Polyline square = ClosedSquare(10);

        Assert.True(PolylineOps.Fillet(square, 3, Plane.WorldXY, 8, out Polyline? filleted).IsSuccess);

        foreach (Point3d point in filleted!.Points)
        {
            Assert.InRange(point.X, -1e-9, 10 + 1e-9);
            Assert.InRange(point.Y, -1e-9, 10 + 1e-9);
        }

        Assert.False(Triangulation.SelfIntersects(filleted.Points[..^1], Plane.WorldXY));
    }

    [Fact]
    public void Fillet_TouchesBothLegsAtTheTangentPoints()
    {
        Polyline square = ClosedSquare(10);

        Assert.True(PolylineOps.Fillet(square, 2, Plane.WorldXY, 1, out Polyline? filleted).IsSuccess);

        // The corner at the origin becomes an arc from (2,0) to (0,2), so both those points must be present.
        bool foundAlongX = false;
        bool foundAlongY = false;

        foreach (Point3d point in filleted!.Points)
        {
            if (PointOps.EpsilonEquals(point, PointOps.Create(2, 0, 0), 1e-9))
            {
                foundAlongX = true;
            }

            if (PointOps.EpsilonEquals(point, PointOps.Create(0, 2, 0), 1e-9))
            {
                foundAlongY = true;
            }
        }

        Assert.True(foundAlongX, "the fillet should meet the leg along X at the tangent distance");
        Assert.True(foundAlongY, "the fillet should meet the leg along Y at the tangent distance");
    }

    [Fact]
    public void Fillet_LeavesTheEndsOfAnOpenPolylineSharp()
    {
        // An end has one leg, so there is no corner there to round.
        Polyline open = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(10, 10, 0),
        ]);

        Assert.True(PolylineOps.Fillet(open, 2, Plane.WorldXY, 4, out Polyline? filleted).IsSuccess);

        Assert.True(PointOps.EpsilonEquals(filleted!.Points[0], PointOps.Create(0, 0, 0), 1e-12));
        Assert.True(PointOps.EpsilonEquals(filleted.Points[^1], PointOps.Create(10, 10, 0), 1e-12));
        Assert.False(PolylineOps.IsClosed(filleted));
    }

    [Fact]
    public void Fillet_LeavesACornerSharpWhenTheRadiusWillNotFitItsLeg()
    {
        // A fillet larger than its own leg would run past the end of it and cross the outline. Reporting is
        // the point: a caller can lower the radius rather than wonder why an edge is still crisp.
        Polyline narrow = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(1, 0, 0),
            PointOps.Create(1, 20, 0),
            PointOps.Create(0, 20, 0),
            PointOps.Create(0, 0, 0),
        ]);

        OperationResult result = PolylineOps.Fillet(
            narrow, radius: 5, plane: Plane.WorldXY, segmentsPerCorner: 4, out Polyline? filleted);

        Assert.True(result.IsPartial, result.ToString());
        Assert.Contains("left sharp", result.Message);
        Assert.NotNull(filleted);
    }

    [Fact]
    public void Fillet_ResolvesTwoNeighboursCompetingForTheSameShortLeg()
    {
        // Both corners want three units of a two-unit leg. The result must not overshoot; whichever corner
        // gives way, the outline has to stay sane.
        Polyline shape = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(2, 0, 0),
            PointOps.Create(2, 20, 0),
            PointOps.Create(0, 20, 0),
            PointOps.Create(0, 0, 0),
        ]);

        OperationResult result = PolylineOps.Fillet(shape, 3, Plane.WorldXY, 8, out Polyline? filleted);

        Assert.True(result.HasOutput);
        Assert.False(Triangulation.SelfIntersects(filleted!.Points[..^1], Plane.WorldXY));

        foreach (Point3d point in filleted.Points)
        {
            Assert.InRange(point.X, -1e-9, 2 + 1e-9);
            Assert.InRange(point.Y, -1e-9, 20 + 1e-9);
        }
    }

    [Fact]
    public void Fillet_SkipsAStraightCornerRatherThanDividingByZero()
    {
        Polyline withCollinearPoint = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(5, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(10, 10, 0),
            PointOps.Create(0, 10, 0),
            PointOps.Create(0, 0, 0),
        ]);

        OperationResult result = PolylineOps.Fillet(
            withCollinearPoint, 2, Plane.WorldXY, 4, out Polyline? filleted);

        Assert.True(result.IsPartial);
        Assert.NotNull(filleted);

        // The collinear point is still there, untouched.
        bool kept = false;

        foreach (Point3d point in filleted.Points)
        {
            if (PointOps.EpsilonEquals(point, PointOps.Create(5, 0, 0), 1e-12))
            {
                kept = true;
            }
        }

        Assert.True(kept);
    }

    [Fact]
    public void Fillet_FeedsStraightIntoAnExtrusion()
    {
        // The reason it exists: a rounded-corner section is what a real board's edge looks like.
        Assert.True(PolylineOps.Fillet(
            ClosedSquare(10), 2, Plane.WorldXY, 6, out Polyline? section).IsSuccess);

        OperationResult extruded = MeshBuilders.CreateExtrusion(
            section!, VectorOps.Create(0, 0, 3), capped: true, out Mesh? mesh);

        Assert.True(extruded.IsSuccess, extruded.ToString());
        Assert.True(RenderBuffers.CreateTriangleIndices(mesh!, out _).IsSuccess);
    }

    [Fact]
    public void Fillet_RejectsANonPositiveRadius()
    {
        Assert.True(PolylineOps.Fillet(ClosedSquare(1), 0, Plane.WorldXY, 4, out _).IsFailed);
        Assert.True(PolylineOps.Fillet(ClosedSquare(1), -1, Plane.WorldXY, 4, out _).IsFailed);
        Assert.True(PolylineOps.Fillet(ClosedSquare(1), 1, Plane.WorldXY, 0, out _).IsFailed);
    }

    [Fact]
    public void Offset_GrowsACounterClockwiseOutlineForAPositiveDistance()
    {
        OperationResult result = PolylineOps.Offset(
            ClosedSquare(10), 1, Plane.WorldXY, out Polyline? offset);

        Assert.True(result.IsSuccess, result.ToString());

        // A 10 by 10 square grown by one becomes 12 by 12, corners still corners.
        Assert.Equal(5, offset!.PointCount);
        Assert.Equal(144, Math.Abs(PolylineOps.SignedArea(offset, Plane.WorldXY)), 9);
    }

    [Fact]
    public void Offset_ShrinksForANegativeDistance()
    {
        Assert.True(PolylineOps.Offset(
            ClosedSquare(10), -2, Plane.WorldXY, out Polyline? offset).IsSuccess);

        Assert.Equal(36, Math.Abs(PolylineOps.SignedArea(offset!, Plane.WorldXY)), 9);
    }

    [Fact]
    public void Offset_KeepsTheCornerCountSoTheShapeStillLooksLikeItself()
    {
        // No arcs are inserted at the corners. That is what makes this right for a wall thickness: an inset
        // panel should look like the panel, not like a rounded version of it.
        Polyline lShape = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(6, 0, 0),
            PointOps.Create(6, 2, 0),
            PointOps.Create(2, 2, 0),
            PointOps.Create(2, 6, 0),
            PointOps.Create(0, 6, 0),
            PointOps.Create(0, 0, 0),
        ]);

        Assert.True(PolylineOps.Offset(lShape, -0.5, Plane.WorldXY, out Polyline? offset).IsSuccess);

        Assert.Equal(lShape.PointCount, offset!.PointCount);
        Assert.True(PolylineOps.IsClosed(offset));
    }

    [Fact]
    public void Offset_PutsEveryEdgeExactlyTheDistanceFromTheOriginal()
    {
        // The real invariant, and stronger than checking area: each offset segment must sit parallel to its
        // original at exactly the distance asked for.
        Polyline square = ClosedSquare(10);
        double distance = 1.5;

        Assert.True(PolylineOps.Offset(square, distance, Plane.WorldXY, out Polyline? offset).IsSuccess);

        for (int i = 0; i < PolylineOps.SegmentCount(square); i++)
        {
            Line original = PolylineOps.Segment(square, i);
            Line moved = PolylineOps.Segment(offset!, i);

            Assert.Equal(distance, LineOps.DistanceTo(original, LineOps.Midpoint(moved)), 9);
        }
    }

    [Fact]
    public void Offset_MovesAnOpenPolylineSidewaysWithoutClosingIt()
    {
        Polyline open = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(10, 10, 0),
        ]);

        Assert.True(PolylineOps.Offset(open, 1, Plane.WorldXY, out Polyline? offset).IsSuccess);

        Assert.Equal(3, offset!.PointCount);
        Assert.False(PolylineOps.IsClosed(offset));

        // The ends move straight out from their own segment, to the right of the way it runs.
        Assert.True(PointOps.EpsilonEquals(offset.Points[0], PointOps.Create(0, -1, 0), 1e-9));
        Assert.True(PointOps.EpsilonEquals(offset.Points[^1], PointOps.Create(11, 10, 0), 1e-9));
    }

    [Fact]
    public void Offset_ReversesSideWhenTheOutlineIsWoundTheOtherWay()
    {
        // Left of travel, not "outward" — an outline has no outward until you know which way it runs.
        Polyline clockwise = PolylineOps.Reversed(ClosedSquare(10));

        Assert.True(PolylineOps.Offset(clockwise, 1, Plane.WorldXY, out Polyline? offset).IsSuccess);

        // Positive grows a counter-clockwise outline, so it shrinks a clockwise one.
        Assert.Equal(64, Math.Abs(PolylineOps.SignedArea(offset!, Plane.WorldXY)), 9);
    }

    [Fact]
    public void Offset_ByZeroReturnsACopy()
    {
        Polyline square = ClosedSquare(10);

        Assert.True(PolylineOps.Offset(square, 0, Plane.WorldXY, out Polyline? offset).IsSuccess);

        Assert.True(PolylineOps.EpsilonEquals(square, offset!));
        Assert.NotSame(square, offset);
    }

    [Fact]
    public void Offset_DoesNotCleanUpAfterItself()
    {
        // Documented limitation, and worth pinning because the artefact looks healthy. A 4 by 4 square shrunk
        // by 3 should be annihilated: there is no outline three units inside it. What comes out instead is a
        // tidy 2 by 2 square, correctly wound, because each pair of opposite edges passed through the other
        // and the two crossings put the orientation back. Nothing about the result says it is wrong.
        Polyline square = ClosedSquare(4);

        Assert.True(PolylineOps.Offset(square, -3, Plane.WorldXY, out Polyline? overshot).IsSuccess);

        Assert.Equal(4, PolylineOps.SignedArea(overshot!, Plane.WorldXY), 9);
        Assert.False(Triangulation.SelfIntersects(overshot!.Points[..^1], Plane.WorldXY));

        // So the check that catches it is on the caller's side: an inward offset that grew, or one whose
        // distance exceeds half the narrowest part, has annihilated something.
        Assert.True(
            Math.Abs(PolylineOps.SignedArea(overshot!, Plane.WorldXY)) > 0,
            "the offset should have collapsed to nothing but did not");
    }

    [Fact]
    public void Offset_ReportsACornerThatFoldsRightBack()
    {
        Polyline spike = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(10, 0, 0),
            PointOps.Create(0, 0.0000000001, 0),
            PointOps.Create(0, 5, 0),
        ]);

        OperationResult result = PolylineOps.Offset(spike, 1, Plane.WorldXY, out Polyline? offset);

        Assert.True(result.HasOutput);
        Assert.NotNull(offset);
    }

    [Fact]
    public void Offset_WorksInATiltedPlane()
    {
        Plane tilted = PlaneOps.CreateFromNormal(
            PointOps.Create(1, 2, 3), VectorOps.Create(1, 1, 2));

        Point3d[] corners =
        [
            PlaneOps.PointAt(tilted, 0, 0),
            PlaneOps.PointAt(tilted, 10, 0),
            PlaneOps.PointAt(tilted, 10, 10),
            PlaneOps.PointAt(tilted, 0, 10),
            PlaneOps.PointAt(tilted, 0, 0),
        ];

        Assert.True(PolylineOps.Offset(
            PolylineOps.Create(corners), 1, tilted, out Polyline? offset).IsSuccess);

        Assert.Equal(144, Math.Abs(PolylineOps.SignedArea(offset!, tilted)), 9);

        // And it stays in the plane it was given.
        foreach (Point3d point in offset!.Points)
        {
            Assert.Equal(0, PlaneOps.DistanceTo(tilted, point), 9);
        }
    }

    [Fact]
    public void Offset_FailsOnAZeroLengthSegment()
    {
        Polyline doubled = PolylineOps.Create(
        [
            PointOps.Create(0, 0, 0),
            PointOps.Create(0, 0, 0),
            PointOps.Create(5, 0, 0),
        ]);

        OperationResult result = PolylineOps.Offset(doubled, 1, Plane.WorldXY, out Polyline? offset);

        Assert.True(result.IsFailed);
        Assert.Null(offset);
        Assert.Contains("no length", result.Message);
    }

    [Fact]
    public void Offset_ThenExtrudeGivesAPanelWithWallThickness()
    {
        // The composition this was built for: an outline and its inset twin, extruded, are a box with a
        // rebate. Both must extrude cleanly.
        Polyline outer = ClosedSquare(10);
        Assert.True(PolylineOps.Offset(outer, -1, Plane.WorldXY, out Polyline? inner).IsSuccess);

        Assert.True(MeshBuilders.CreateExtrusion(
            outer, VectorOps.Create(0, 0, 2), true, out Mesh? outerMesh).IsSuccess);

        Assert.True(MeshBuilders.CreateExtrusion(
            inner!, VectorOps.Create(0, 0, 2), true, out Mesh? innerMesh).IsSuccess);

        Assert.Equal(6, outerMesh!.FaceCount);
        Assert.Equal(6, innerMesh!.FaceCount);
    }
}
