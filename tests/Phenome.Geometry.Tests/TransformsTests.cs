using Phenome.Geometry;

namespace Phenome.Geometry.Tests;

public class TransformsTests
{
    private static readonly TMatrix Composite =
        Transforms.Translate(1, 2, 3) * Transforms.Rotate(Vector3d.ZAxis, 0.7) * Transforms.Scale(2);

    private static readonly TMatrix Perspective = Transforms.Create(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 1, 0);

    [Fact]
    public void Identity_LeavesGeometryAlone()
    {
        Point3d point = PointOps.Create(3, -4, 5);
        Vector3d vector = VectorOps.Create(1, 2, 3);
        Line line = LineOps.Create(point, Point3d.Origin);

        Assert.Equal(point, PointOps.Transform(point, TMatrix.Identity));
        Assert.Equal(vector, VectorOps.Transform(vector, TMatrix.Identity));
        Assert.Equal(line, LineOps.Transform(line, TMatrix.Identity));
        Assert.True(Transforms.IsIdentity(TMatrix.Identity));
        Assert.True(Transforms.IsAffine(TMatrix.Identity));
        Assert.True(Transforms.IsValid(TMatrix.Identity));
    }

    [Fact]
    public void IsValid_RejectsNaNEntries()
    {
        Assert.True(Transforms.IsValid(TMatrix.Identity));
        Assert.True(Transforms.IsValid(TMatrix.Zero));
        Assert.False(Transforms.IsValid(TMatrix.Unset));
    }

    [Fact]
    public void Translate_MovesPointsButNotVectors()
    {
        TMatrix translation = Transforms.Translate(10, 20, 30);

        Assert.Equal(
            PointOps.Create(11, 22, 33),
            PointOps.Transform(PointOps.Create(1, 2, 3), translation));

        Assert.Equal(Vector3d.XAxis, VectorOps.Transform(Vector3d.XAxis, translation));
    }

    [Fact]
    public void Translate_AgreesBetweenItsVectorAndComponentOverloads()
    {
        Assert.Equal(
            Transforms.Translate(1, 2, 3),
            Transforms.Translate(VectorOps.Create(1, 2, 3)));
    }

    [Fact]
    public void GetTranslation_ReadsBackTheLastColumn()
    {
        Assert.Equal(
            VectorOps.Create(10, 20, 30),
            Transforms.GetTranslation(Transforms.Translate(10, 20, 30)));

        Assert.Equal(Vector3d.Zero, Transforms.GetTranslation(TMatrix.Identity));
    }

    [Fact]
    public void Scale_AboutTheOriginScalesEveryAxis()
    {
        Assert.Equal(
            PointOps.Create(2, 4, 6),
            PointOps.Transform(PointOps.Create(1, 2, 3), Transforms.Scale(2)));

        Assert.Equal(
            PointOps.Create(2, 6, 12),
            PointOps.Transform(PointOps.Create(1, 2, 3), Transforms.Scale(2, 3, 4)));
    }

    [Fact]
    public void Scale_AboutACentreLeavesThatCentreFixed()
    {
        Point3d centre = PointOps.Create(5, 5, 5);
        TMatrix scale = Transforms.Scale(centre, 3);

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(centre, scale), centre, 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(PointOps.Create(6, 5, 5), scale),
            PointOps.Create(8, 5, 5),
            1e-12));
    }

    [Fact]
    public void Rotate_AQuarterTurnAboutZTakesXToY()
    {
        TMatrix rotation = Transforms.Rotate(Vector3d.ZAxis, Math.PI / 2);

        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Transform(Vector3d.XAxis, rotation), Vector3d.YAxis, 1e-12));

        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Transform(Vector3d.YAxis, rotation), -Vector3d.XAxis, 1e-12));

        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Transform(Vector3d.ZAxis, rotation), Vector3d.ZAxis, 1e-12));
    }

    [Fact]
    public void Rotate_DoesNotRequireANormalisedAxis()
    {
        Assert.True(Transforms.EpsilonEquals(
            Transforms.Rotate(Vector3d.ZAxis, 0.9),
            Transforms.Rotate(Vector3d.ZAxis * 17, 0.9),
            1e-12));
    }

    [Fact]
    public void Rotate_AboutACentreLeavesThatCentreFixed()
    {
        Point3d centre = PointOps.Create(2, 3, 4);
        TMatrix rotation = Transforms.Rotate(centre, Vector3d.ZAxis, Math.PI / 2);

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(centre, rotation), centre, 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(PointOps.Create(3, 3, 4), rotation),
            PointOps.Create(2, 4, 4),
            1e-12));
    }

    [Fact]
    public void Rotate_PreservesLengthsAndAngles()
    {
        TMatrix rotation = Transforms.Rotate(VectorOps.Create(1, 2, 3), 1.234);
        Vector3d a = VectorOps.Create(4, -5, 6);
        Vector3d b = VectorOps.Create(-1, 0, 2);

        Assert.Equal(
            VectorOps.Length(a),
            VectorOps.Length(VectorOps.Transform(a, rotation)),
            1e-12);

        Assert.Equal(
            VectorOps.AngleBetween(a, b),
            VectorOps.AngleBetween(
                VectorOps.Transform(a, rotation),
                VectorOps.Transform(b, rotation)),
            1e-12);
    }

    [Fact]
    public void Rotate_RejectsADegenerateAxis()
    {
        Assert.Throws<ArgumentException>(() => Transforms.Rotate(Vector3d.Zero, 1.0));
        Assert.Throws<ArgumentException>(() => Transforms.Rotate(Vector3d.Unset, 1.0));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 1, 0)]
    [InlineData(1, 0, 0, 1, 1, 0)]
    [InlineData(0, 0, 1, 1, 2, 3)]
    [InlineData(3, -4, 5, -2, 7, 1)]
    public void RotateFromTo_TakesTheFirstDirectionOntoTheSecond(
        double fx, double fy, double fz,
        double tx, double ty, double tz)
    {
        Vector3d from = VectorOps.Create(fx, fy, fz);
        Vector3d to = VectorOps.Create(tx, ty, tz);

        TMatrix rotation = Transforms.Rotate(from, to);
        Vector3d rotated = VectorOps.Transform(VectorOps.Normalized(from), rotation);

        Assert.True(VectorOps.EpsilonEquals(rotated, VectorOps.Normalized(to), 1e-12));
    }

    [Fact]
    public void RotateFromTo_HandlesAlignedDirections()
    {
        Assert.True(Transforms.IsIdentity(
            Transforms.Rotate(Vector3d.XAxis, Vector3d.XAxis * 4), 1e-12));
    }

    [Fact]
    public void RotateFromTo_HandlesOpposedDirections()
    {
        // No shortest arc exists here, so the previous hand-rolled plane-to-plane code produced a
        // degenerate axis and a NaN matrix. A half turn about any perpendicular is a valid answer.
        TMatrix rotation = Transforms.Rotate(Vector3d.XAxis, -Vector3d.XAxis);

        Assert.True(Transforms.IsValid(rotation));
        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Transform(Vector3d.XAxis, rotation), -Vector3d.XAxis, 1e-12));
    }

    [Fact]
    public void RotateFromTo_RejectsDegenerateDirections()
    {
        Assert.Throws<ArgumentException>(() => Transforms.Rotate(Vector3d.Zero, Vector3d.XAxis));
        Assert.False(Transforms.TryRotate(Vector3d.XAxis, Vector3d.Zero, out TMatrix? rotation));
        Assert.Null(rotation);
    }

    [Fact]
    public void FrameToWorld_MapsTheWorldBasisOntoTheGivenFrame()
    {
        Point3d origin = PointOps.Create(1, 1, 1);
        TMatrix frame = Transforms.FrameToWorld(
            Vector3d.XAxis * 2, Vector3d.YAxis, Vector3d.ZAxis, origin);

        Assert.Equal(origin, PointOps.Transform(Point3d.Origin, frame));
        Assert.Equal(
            PointOps.Create(3, 1, 1),
            PointOps.Transform(PointOps.Create(1, 0, 0), frame));

        Assert.Equal(
            VectorOps.Create(2, 0, 0), VectorOps.Transform(Vector3d.XAxis, frame));
    }

    [Fact]
    public void PlaneToWorld_MapsTheWorldBasisOntoThePlaneFrame()
    {
        Plane plane = PlaneOps.CreateFromNormal(
            PointOps.Create(1, 2, 3), VectorOps.Create(1, 1, 1));

        TMatrix toWorld = Transforms.PlaneToWorld(plane);

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(Point3d.Origin, toWorld), plane.Origin, 1e-12));

        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Transform(Vector3d.XAxis, toWorld), plane.XAxis, 1e-12));

        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Transform(Vector3d.ZAxis, toWorld), plane.ZAxis, 1e-12));
    }

    [Fact]
    public void WorldToPlane_IsTheExactInverseOfPlaneToWorld()
    {
        Plane plane = PlaneOps.CreateFromNormal(
            PointOps.Create(-4, 7, 2), VectorOps.Create(3, -1, 2));

        TMatrix toWorld = Transforms.PlaneToWorld(plane);
        TMatrix toPlane = Transforms.WorldToPlane(plane);

        Assert.True(Transforms.EpsilonEquals(toWorld * toPlane, TMatrix.Identity, 1e-12));
        Assert.True(Transforms.EpsilonEquals(toPlane * toWorld, TMatrix.Identity, 1e-12));
    }

    [Fact]
    public void WorldToPlane_YieldsFrameCoordinates()
    {
        Plane plane = PlaneOps.CreateFromNormal(
            PointOps.Create(1, 2, 3), VectorOps.Create(1, 1, 1));

        Point3d onPlane = PlaneOps.PointAt(plane, 2.5, -1.25);
        Point3d local = PointOps.Transform(onPlane, Transforms.WorldToPlane(plane));

        // X and Y come out as the frame parameters; Z is the signed distance, zero for a point on it.
        Assert.Equal(2.5, local.X, 1e-12);
        Assert.Equal(-1.25, local.Y, 1e-12);
        Assert.Equal(0.0, local.Z, 1e-12);
    }

    [Fact]
    public void PlaneToPlane_CarriesFrameCoordinatesAcross()
    {
        Plane from = PlaneOps.CreateFromNormal(PointOps.Create(1, 1, 1), Vector3d.ZAxis);
        Plane to = PlaneOps.CreateFromNormal(
            PointOps.Create(-5, 3, 8), VectorOps.Create(1, 2, 3));

        TMatrix map = Transforms.PlaneToPlane(from, to);

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(from.Origin, map), to.Origin, 1e-12));

        Assert.True(PointOps.EpsilonEquals(
            PointOps.Transform(PlaneOps.PointAt(from, 3, -2), map),
            PlaneOps.PointAt(to, 3, -2),
            1e-12));
    }

    [Fact]
    public void PlaneToPlane_SurvivesAnOpposedNormal()
    {
        // The previous hand-rolled version composed rotations derived from unsigned angles and
        // produced a NaN matrix whenever the source normal was opposite world Z.
        Plane facingDown = PlaneOps.CreateFromNormal(Point3d.Origin, -Vector3d.ZAxis);

        TMatrix map = Transforms.PlaneToPlane(facingDown, Plane.WorldXY);

        Assert.True(Transforms.IsValid(map));
        Assert.True(VectorOps.EpsilonEquals(
            VectorOps.Transform(facingDown.Normal, map), Plane.WorldXY.Normal, 1e-12));
    }

    [Fact]
    public void PlaneToPlane_ToItselfIsTheIdentity()
    {
        Plane plane = PlaneOps.CreateFromNormal(
            PointOps.Create(4, -2, 9), VectorOps.Create(2, 5, -1));

        Assert.True(Transforms.IsIdentity(Transforms.PlaneToPlane(plane, plane), 1e-12));
    }

    [Fact]
    public void Determinant_MatchesKnownValues()
    {
        Assert.Equal(1.0, Transforms.Determinant(TMatrix.Identity), 1e-12);
        Assert.Equal(24.0, Transforms.Determinant(Transforms.Scale(2, 3, 4)), 1e-12);
        Assert.Equal(8.0, Transforms.Determinant(Transforms.Scale(2)), 1e-12);
        Assert.Equal(
            1.0,
            Transforms.Determinant(Transforms.Rotate(VectorOps.Create(1, 1, 1), 0.83)),
            1e-12);
        Assert.Equal(1.0, Transforms.Determinant(Transforms.Translate(9, 9, 9)), 1e-12);
    }

    [Fact]
    public void Determinant_IsZeroForATransformThatCollapsesSpace()
    {
        Assert.Equal(0.0, Transforms.Determinant(Transforms.Scale(1, 1, 0)), 1e-12);
        Assert.Equal(0.0, Transforms.Determinant(TMatrix.Zero), 1e-12);
    }

    [Fact]
    public void Determinant_MatchesAHandComputedReference()
    {
        // Column 2 holds a single non-zero entry, M32 = 1, so expanding along it gives
        // det = (-1)^(3+2) * 1 * minor(3,2) = -1 * -30 = 30, where minor(3,2) is
        // det [[1, 2, -1], [3, 0, 5], [1, 5, 0]] = -25 + 10 - 15 = -30.
        TMatrix known = Transforms.Create(
            1, 0, 2, -1,
            3, 0, 0, 5,
            2, 1, 4, -3,
            1, 0, 5, 0);

        Assert.Equal(30.0, Transforms.Determinant(known), 1e-9);
    }

    [Fact]
    public void TryInvert_UndoesTranslationRotationAndScale()
    {
        TMatrix[] transforms =
        [
            Transforms.Translate(3, -4, 5),
            Transforms.Rotate(VectorOps.Create(1, 2, 3), 1.1),
            Transforms.Scale(2, 4, 8),
            Transforms.Scale(PointOps.Create(1, 1, 1), 3),
            Composite,
        ];

        foreach (TMatrix transform in transforms)
        {
            Assert.True(Transforms.TryInvert(transform, out TMatrix? inverse));
            Assert.NotNull(inverse);
            Assert.True(Transforms.EpsilonEquals(transform * inverse.Value, TMatrix.Identity, 1e-12));
            Assert.True(Transforms.EpsilonEquals(inverse.Value * transform, TMatrix.Identity, 1e-12));
        }
    }

    [Fact]
    public void TryInvert_HandlesAnArbitraryNonAffineMatrix()
    {
        // Every other inverse test uses an affine matrix, where the bottom row is (0, 0, 0, 1). The
        // cofactor expansion is general, so it needs a case with a fully populated bottom row too.
        TMatrix general = Transforms.Create(
            2, 3, 1, 5,
            1, 0, 3, 1,
            0, 2, -3, 2,
            4, 1, 2, 3);

        Assert.NotEqual(0.0, Transforms.Determinant(general));
        Assert.False(Transforms.IsAffine(general));

        Assert.True(Transforms.TryInvert(general, out TMatrix? inverse));
        Assert.NotNull(inverse);
        Assert.True(Transforms.EpsilonEquals(general * inverse.Value, TMatrix.Identity, 1e-12));
        Assert.True(Transforms.EpsilonEquals(inverse.Value * general, TMatrix.Identity, 1e-12));
    }

    [Fact]
    public void TryInvert_RoundTripsAPointBackToWhereItStarted()
    {
        Point3d point = PointOps.Create(5, -3, 2);

        Point3d there = PointOps.Transform(point, Composite);
        Point3d back = PointOps.Transform(there, Transforms.Inverted(Composite));

        Assert.True(PointOps.EpsilonEquals(back, point, 1e-12));
        Assert.False(PointOps.EpsilonEquals(there, point, 1e-6));
    }

    [Fact]
    public void TryInvert_FailsForASingularMatrixAndYieldsNull()
    {
        // Deliberately not the identity: substituting a usable fallback would leave geometry
        // untransformed and read as success, producing a plausible but wrong model.
        Assert.False(Transforms.TryInvert(Transforms.Scale(1, 1, 0), out TMatrix? inverse));
        Assert.Null(inverse);

        Assert.False(Transforms.TryInvert(TMatrix.Zero, out _));
        Assert.False(Transforms.TryInvert(TMatrix.Unset, out _));
        Assert.Throws<InvalidOperationException>(() => Transforms.Inverted(Transforms.Scale(0)));
    }

    [Fact]
    public void IgnoringTheTryInvertResult_FailsAtTheCallSiteRatherThanLater()
    {
        Transforms.TryInvert(Transforms.Scale(1, 1, 0), out TMatrix? inverse);

        Assert.Throws<InvalidOperationException>(() => inverse!.Value);
    }

    [Fact]
    public void Transposed_ExchangesRowsAndColumnsAndIsItsOwnInverse()
    {
        TMatrix transposed = Transforms.Transposed(Composite);

        Assert.Equal(Composite.M14, transposed.M41);
        Assert.Equal(Composite.M23, transposed.M32);
        Assert.True(Transforms.EpsilonEquals(Transforms.Transposed(transposed), Composite, 1e-12));
    }

    [Fact]
    public void Transposed_OfARotationIsItsInverse()
    {
        TMatrix rotation = Transforms.Rotate(VectorOps.Create(2, -1, 4), 0.65);

        Assert.True(Transforms.EpsilonEquals(
            Transforms.Transposed(rotation), Transforms.Inverted(rotation), 1e-12));
    }

    [Fact]
    public void IsAffine_DetectsAPerspectiveBottomRow()
    {
        Assert.True(Transforms.IsAffine(Composite));
        Assert.True(Transforms.IsAffine(Transforms.Translate(1, 2, 3)));
        Assert.False(Transforms.IsAffine(Perspective));
    }

    [Fact]
    public void RowMajor_RoundTripsThroughTheMatrixAndBack()
    {
        double[] values = Transforms.ToRowMajor(Composite);

        Assert.Equal(16, values.Length);
        Assert.Equal(Composite, Transforms.CreateFromRowMajor(values));
        Assert.Equal(Composite.M11, values[0]);
        Assert.Equal(Composite.M14, values[3]);
        Assert.Equal(Composite.M44, values[15]);
    }

    [Fact]
    public void CreateFromRowMajor_RejectsTheWrongNumberOfValues()
    {
        Assert.Throws<ArgumentException>(() => Transforms.CreateFromRowMajor(new double[15]));
        Assert.Throws<ArgumentException>(() => Transforms.CreateFromRowMajor(new double[17]));
    }
}
