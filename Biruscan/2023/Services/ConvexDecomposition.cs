using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace Biruscan.Services
{
    /// <summary>
    /// Decomposes a 2D polygon (expressed in a view-local coordinate system)
    /// into one or more convex sub-polygons.
    ///
    /// This is needed because Revit's PointCloudFilterFactory.CreateMultiPlaneFilter
    /// intersects all half-planes (AND logic), which can only define a CONVEX region.
    /// A concave polygon must be split into convex pieces so each piece can have
    /// its own multi-plane filter, and the results are unioned.
    ///
    /// Algorithm:
    ///   1. If the polygon is already convex → return it as a single piece (fast path).
    ///   2. Otherwise, triangulate via ear-clipping.
    ///   3. Greedily merge adjacent triangles into larger convex polygons to
    ///      reduce the total number of sub-filters.
    /// </summary>
    internal static class ConvexDecomposition
    {
        /// <summary>
        /// Returns true if the 2D polygon (XY only) is convex.
        /// Points must be in counter-clockwise order.
        /// </summary>
        public static bool IsConvex(IList<XYZ> pts)
        {
            int n = pts.Count;
            if (n < 3) return false;

            for (int i = 0; i < n; i++)
            {
                XYZ a = pts[i];
                XYZ b = pts[(i + 1) % n];
                XYZ c = pts[(i + 2) % n];
                double cross = Cross2D(b - a, c - b);
                if (cross < -1e-10) // clockwise turn → concave vertex
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 2D convex hull (XY only) of a point set, returned CCW.
        /// Used for polygonal clips so we apply ONE multi-plane filter on a single
        /// PointCloudInstance — no clone instances (which inflate point-cloud count).
        /// Monotone chain algorithm.
        /// </summary>
        public static List<XYZ> ConvexHull2D(IList<XYZ> pts)
        {
            if (pts == null || pts.Count == 0) return new List<XYZ>();
            if (pts.Count <= 2) return new List<XYZ>(pts);

            // Unique by XY (keep first Z)
            var uniq = new List<XYZ>(pts.Count);
            var seen = new HashSet<string>();
            foreach (XYZ p in pts)
            {
                string k = $"{p.X:F5},{p.Y:F5}";
                if (seen.Add(k)) uniq.Add(p);
            }
            if (uniq.Count <= 2) return uniq;

            uniq.Sort((a, b) =>
            {
                int cx = a.X.CompareTo(b.X);
                return cx != 0 ? cx : a.Y.CompareTo(b.Y);
            });

            var lower = new List<XYZ>();
            foreach (XYZ p in uniq)
            {
                while (lower.Count >= 2 &&
                       Cross2D(lower[lower.Count - 1] - lower[lower.Count - 2], p - lower[lower.Count - 1]) <= 1e-12)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<XYZ>();
            for (int i = uniq.Count - 1; i >= 0; i--)
            {
                XYZ p = uniq[i];
                while (upper.Count >= 2 &&
                       Cross2D(upper[upper.Count - 1] - upper[upper.Count - 2], p - upper[upper.Count - 1]) <= 1e-12)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            var hull = new List<XYZ>(lower.Count + upper.Count);
            hull.AddRange(lower);
            hull.AddRange(upper);
            return hull.Count >= 3 ? hull : uniq;
        }

        /// <summary>
        /// Decomposes a simple (non-self-intersecting) polygon into convex sub-polygons.
        /// Input points are in CCW order (only XY coordinates are used).
        /// Returns a list of convex polygons (each as a list of XYZ in CCW order).
        /// If the polygon is already convex, returns a single-element list containing
        /// the original polygon — zero overhead for the common case.
        /// </summary>
        public static List<List<XYZ>> Decompose(IList<XYZ> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                throw new ArgumentException("Polygon must have at least 3 vertices.");

            // Fast path: already convex
            if (IsConvex(polygon))
                return new List<List<XYZ>> { new List<XYZ>(polygon) };

            // Step 1: Triangulate via ear-clipping
            List<int[]> triangles = EarClipTriangulate(polygon);

            // Step 2: Greedily merge adjacent triangles into larger convex polygons
            List<List<XYZ>> convexPieces = MergeTriangles(polygon, triangles);

            return convexPieces;
        }

        // ======================================================================
        // Ear-Clipping Triangulation
        // ======================================================================

        /// <summary>
        /// Triangulates a simple polygon using the ear-clipping algorithm.
        /// Returns a list of triangles, each represented as an array of 3 indices
        /// into the original polygon vertex list.
        /// </summary>
        private static List<int[]> EarClipTriangulate(IList<XYZ> polygon)
        {
            int n = polygon.Count;
            var triangles = new List<int[]>();

            // Build a working list of indices
            var indices = new List<int>(n);
            for (int i = 0; i < n; i++) indices.Add(i);

            int safety = n * n; // prevent infinite loop on degenerate input
            while (indices.Count > 3 && safety-- > 0)
            {
                bool earFound = false;
                int count = indices.Count;
                for (int i = 0; i < count; i++)
                {
                    int prev = indices[(i - 1 + count) % count];
                    int curr = indices[i];
                    int next = indices[(i + 1) % count];

                    // Check if this vertex is a "convex" vertex (left turn)
                    XYZ a = polygon[prev];
                    XYZ b = polygon[curr];
                    XYZ c = polygon[next];

                    if (Cross2D(b - a, c - b) < 1e-10)
                        continue; // reflex vertex — not an ear

                    // Check no other vertex falls inside this triangle
                    bool isEar = true;
                    for (int j = 0; j < count; j++)
                    {
                        int idx = indices[j];
                        if (idx == prev || idx == curr || idx == next)
                            continue;
                        if (PointInTriangle2D(polygon[idx], a, b, c))
                        {
                            isEar = false;
                            break;
                        }
                    }

                    if (isEar)
                    {
                        triangles.Add(new int[] { prev, curr, next });
                        indices.RemoveAt(i);
                        earFound = true;
                        break;
                    }
                }

                if (!earFound)
                    break; // degenerate polygon — return what we have
            }

            // Last remaining triangle
            if (indices.Count == 3)
                triangles.Add(new int[] { indices[0], indices[1], indices[2] });

            return triangles;
        }

        // ======================================================================
        // Greedy Triangle Merging
        // ======================================================================

        /// <summary>
        /// Greedily merges adjacent triangles into larger convex polygons.
        /// Two triangles that share an edge can be merged if the result is still convex.
        /// This reduces the number of convex sub-filters needed.
        /// </summary>
        private static List<List<XYZ>> MergeTriangles(IList<XYZ> polygon, List<int[]> triangles)
        {
            // Build adjacency: for each triangle, which triangles share an edge
            int tCount = triangles.Count;

            // Represent each polygon piece as a list of vertex indices (initially triangles)
            var pieces = new List<List<int>>(tCount);
            var alive = new bool[tCount];
            for (int i = 0; i < tCount; i++)
            {
                pieces.Add(new List<int>(triangles[i]));
                alive[i] = true;
            }

            // Keep trying to merge until no more merges possible
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < pieces.Count && !merged; i++)
                {
                    if (!alive[i]) continue;
                    for (int j = i + 1; j < pieces.Count && !merged; j++)
                    {
                        if (!alive[j]) continue;

                        List<int> mergedPoly = TryMerge(pieces[i], pieces[j]);
                        if (mergedPoly != null && IsConvexByIndices(polygon, mergedPoly))
                        {
                            pieces[i] = mergedPoly;
                            alive[j] = false;
                            merged = true;
                        }
                    }
                }
            }

            // Build result
            var result = new List<List<XYZ>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (!alive[i]) continue;
                var poly = new List<XYZ>();
                foreach (int idx in pieces[i])
                    poly.Add(polygon[idx]);
                result.Add(poly);
            }
            return result;
        }

        /// <summary>
        /// Tries to merge two convex polygons (given as index lists) that share exactly
        /// one edge. Returns the merged polygon (as index list) or null if they don't
        /// share an edge.
        /// </summary>
        private static List<int> TryMerge(List<int> polyA, List<int> polyB)
        {
            int nA = polyA.Count;
            int nB = polyB.Count;

            // Find a shared edge: consecutive pair in A that also appears (reversed) in B
            for (int i = 0; i < nA; i++)
            {
                int a1 = polyA[i];
                int a2 = polyA[(i + 1) % nA];

                for (int j = 0; j < nB; j++)
                {
                    int b1 = polyB[j];
                    int b2 = polyB[(j + 1) % nB];

                    // Shared edge: a1-a2 in A corresponds to b2-b1 in B (reversed winding)
                    if (a1 == b2 && a2 == b1)
                    {
                        // Merge: take all of A, then insert B's vertices (excluding the shared edge endpoints)
                        // Walk A up to a2, then walk B starting after b1, skip b2
                        var merged = new List<int>();

                        // Add vertices of A from after the shared edge end to before the shared edge start
                        for (int k = 0; k < nA; k++)
                        {
                            int idx = (i + 1 + k) % nA;
                            merged.Add(polyA[idx]);
                        }
                        // Now merged ends with polyA[i] = a1 = b2
                        // Remove the last element (a1 = b2) if B has more vertices to insert
                        // Insert B's vertices that are not part of the shared edge
                        // B goes: ... b1(=a2), [vertices not on shared edge], b2(=a1) ...
                        // We want vertices from after b2 to before b1 (exclusive both endpoints)
                        int bInsertStart = (j + 2) % nB; // first vertex after b2
                        int bInsertCount = nB - 2; // all vertices except b1 and b2

                        if (bInsertCount > 0)
                        {
                            for (int k = 0; k < bInsertCount; k++)
                            {
                                int idx = (bInsertStart + k) % nB;
                                merged.Add(polyB[idx]);
                            }
                        }

                        return merged;
                    }
                }
            }

            return null; // No shared edge found
        }

        /// <summary>
        /// Checks if a polygon (given as indices into the original vertex array) is convex.
        /// </summary>
        private static bool IsConvexByIndices(IList<XYZ> vertices, List<int> indices)
        {
            int n = indices.Count;
            if (n < 3) return false;

            for (int i = 0; i < n; i++)
            {
                XYZ a = vertices[indices[i]];
                XYZ b = vertices[indices[(i + 1) % n]];
                XYZ c = vertices[indices[(i + 2) % n]];
                double cross = Cross2D(b - a, c - b);
                if (cross < -1e-10)
                    return false;
            }
            return true;
        }

        // ======================================================================
        // 2D Geometry Helpers
        // ======================================================================

        /// <summary>
        /// 2D cross product (Z component of 3D cross product, using only X and Y).
        /// Positive = counter-clockwise turn, Negative = clockwise turn.
        /// </summary>
        private static double Cross2D(XYZ a, XYZ b)
        {
            return a.X * b.Y - a.Y * b.X;
        }

        /// <summary>
        /// Tests if point P lies inside triangle ABC (2D, using only X and Y).
        /// Uses barycentric coordinate method.
        /// </summary>
        private static bool PointInTriangle2D(XYZ p, XYZ a, XYZ b, XYZ c)
        {
            double d1 = Sign2D(p, a, b);
            double d2 = Sign2D(p, b, c);
            double d3 = Sign2D(p, c, a);

            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

            return !(hasNeg && hasPos);
        }

        private static double Sign2D(XYZ p1, XYZ p2, XYZ p3)
        {
            return (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
        }

        // ======================================================================
        // Plane Generation for Convex Sub-Polygons
        // ======================================================================

        /// <summary>
        /// For each convex sub-polygon (in view-local 2D space), generates the set
        /// of half-planes (in world space) that define that convex region,
        /// including front/back depth caps.
        /// </summary>
        /// <param name="convexPieces">Convex sub-polygons in view-local space (CCW).</param>
        /// <param name="viewTransform">Transform from view-local to world space.</param>
        /// <param name="depthMin">Z-depth min in view-local space.</param>
        /// <param name="depthMax">Z-depth max in view-local space.</param>
        /// <returns>A list of plane-lists — one set of planes per convex piece.</returns>
        public static List<List<Plane>> GeneratePlaneSets(
            List<List<XYZ>> convexPieces,
            Transform viewTransform,
            double depthMin,
            double depthMax)
        {
            var result = new List<List<Plane>>();

            XYZ localZ = new XYZ(0, 0, 1);
            XYZ worldZ = viewTransform.OfVector(localZ);

            foreach (var piece in convexPieces)
            {
                var planes = new List<Plane>();
                int n = piece.Count;

                // Edge planes (inward-pointing normals)
                for (int i = 0; i < n; i++)
                {
                    XYZ p1 = piece[i];
                    XYZ p2 = piece[(i + 1) % n];
                    XYZ edge = p2 - p1;
                    XYZ localNormal = new XYZ(-edge.Y, edge.X, 0).Normalize();
                    XYZ worldNormal = viewTransform.OfVector(localNormal);
                    XYZ worldOrigin = viewTransform.OfPoint(p1);
                    planes.Add(Plane.CreateByNormalAndOrigin(worldNormal, worldOrigin));
                }

                // Depth cap planes
                planes.Add(Plane.CreateByNormalAndOrigin(worldZ,
                    viewTransform.OfPoint(new XYZ(0, 0, depthMin))));
                planes.Add(Plane.CreateByNormalAndOrigin(-worldZ,
                    viewTransform.OfPoint(new XYZ(0, 0, depthMax))));

                result.Add(planes);
            }

            return result;
        }
    }
}
