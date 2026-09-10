using System.Collections.Generic;
using UnityEngine;

namespace HappyHarvest
{
    // Four-way grid search. Both destinations and the swept body between cells must be clear.
    public sealed class FarmPathfinder
    {
        public static readonly Vector3Int[] Directions =
            { Vector3Int.up, Vector3Int.right, Vector3Int.down, Vector3Int.left };
        readonly TerrainManager terrain;
        readonly Transform owner;
        readonly Vector2 offset;
        readonly float radius;
        readonly int mask;

        public FarmPathfinder(TerrainManager terrain, Transform owner, Vector2 offset, float radius, int mask)
        {
            this.terrain = terrain;
            this.owner = owner;
            this.offset = offset;
            this.radius = radius;
            this.mask = mask;
        }

        bool Blocks(Collider2D collider)
        {
            return collider != null && !collider.isTrigger && !collider.usedByComposite &&
                   !collider.transform.IsChildOf(owner);
        }

        public Vector2 Center(Vector3Int cell) => terrain.Grid.GetCellCenterWorld(cell);

        public bool CanStand(Vector3Int cell)
        {
            var position = Center(cell);
            var surface = GameManager.Instance.WalkSurfaceTilemap;
            if (surface == null || !surface.HasTile(surface.WorldToCell(position))) return false;
            foreach (var hit in Physics2D.OverlapCircleAll(position + offset, radius, mask))
                if (Blocks(hit)) return false;
            return true;
        }

        public bool CanMove(Vector2 from, Vector2 to)
        {
            var delta = to - from;
            if (delta.sqrMagnitude < 0.000001f) return true;
            foreach (var hit in Physics2D.CircleCastAll(from + offset, radius, delta.normalized, delta.magnitude, mask))
                if (Blocks(hit.collider)) return false;
            return true;
        }

        public bool TryFind(Vector2 origin, Vector3Int target, out List<Vector2> path)
        {
            path = null;
            var start = terrain.Grid.WorldToCell(origin);
            start.z = 0;
            var goals = new HashSet<Vector3Int>();
            foreach (var direction in Directions)
                if (CanStand(target + direction)) goals.Add(target + direction);
            if (goals.Count == 0) return false;

            var queue = new Queue<Vector3Int>();
            var parent = new Dictionary<Vector3Int, Vector3Int>();
            var standable = new Dictionary<Vector3Int, bool>();
            queue.Enqueue(start);
            parent[start] = start;
            while (queue.Count > 0 && parent.Count <= 12000)
            {
                var cell = queue.Dequeue();
                if (goals.Contains(cell))
                {
                    path = new List<Vector2>();
                    var cursor = cell;
                    while (cursor != start)
                    {
                        path.Add(Center(cursor));
                        cursor = parent[cursor];
                    }
                    path.Reverse();
                    // The actor need not begin exactly at a cell center.
                    if (path.Count == 0) path.Add(Center(start));
                    return CanMove(origin, path[0]);
                }
                foreach (var direction in Directions)
                {
                    var next = cell + direction;
                    if (next == target || parent.ContainsKey(next)) continue;
                    if (!standable.TryGetValue(next, out var clear))
                        standable[next] = clear = CanStand(next);
                    if (!clear || !CanMove(cell == start ? origin : Center(cell), Center(next))) continue;
                    parent[next] = cell;
                    queue.Enqueue(next);
                }
            }
            return false;
        }
    }
}
