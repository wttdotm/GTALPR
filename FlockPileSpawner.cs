using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace FlockSurveillance
{
    internal sealed class FlockPileSpawner : IDisposable
    {
        private const int PropCount = 200;
        private const int GridWidth = 5;
        private const float GridSpacing = 1.2f;
        private const float LayerSpacing = 3.5f;
        private const float ForwardDistance = 12f;
        private const float InitialHeight = 12f;
        private const int RequiredPropHeadroom = PropCount + 16;

        private readonly string _modelName;
        private readonly List<Prop> _props = new List<Prop>();

        public FlockPileSpawner(string modelName)
        {
            _modelName = modelName;
        }

        public bool TrySpawn(
            Ped anchor,
            out int spawnedCount,
            out string error
        )
        {
            spawnedCount = 0;
            error = null;
            Clear();

            if (anchor == null || !anchor.Exists())
            {
                error = "The player character is unavailable.";
                return false;
            }

            if (
                World.PropCapacity - World.PropCount <
                RequiredPropHeadroom
            )
            {
                error =
                    "GTA does not have enough prop-pool headroom for " +
                    PropCount + " Flockfragments.";
                return false;
            }

            Model model = new Model(_modelName);

            if (!model.IsValid || !model.IsInCdImage || !model.IsProp)
            {
                error = "The " + _modelName + " model is unavailable.";
                return false;
            }

            if (!model.Request(1000))
            {
                error = "The " + _modelName + " model did not load.";
                return false;
            }

            try
            {
                Vector3 center =
                    anchor.Position +
                    (anchor.ForwardVector * ForwardDistance);

                for (int index = 0; index < PropCount; index++)
                {
                    int slot = index % (GridWidth * GridWidth);
                    int layer = index / (GridWidth * GridWidth);
                    float x =
                        ((slot % GridWidth) - 2) * GridSpacing;
                    float y =
                        ((slot / GridWidth) - 2) * GridSpacing;

                    Vector3 position =
                        center +
                        new Vector3(
                            x,
                            y,
                            InitialHeight + (layer * LayerSpacing)
                        );

                    Vector3 rotation = new Vector3(
                        (index * 37) % 360,
                        (index * 71) % 360,
                        (index * 113) % 360
                    );

                    Prop prop = World.CreatePropNoOffset(
                        model,
                        position,
                        rotation,
                        true
                    );

                    if (prop == null || !prop.Exists())
                    {
                        error =
                            "GTA stopped creating the pile after " +
                            spawnedCount + " props.";
                        break;
                    }

                    prop.IsPersistent = true;
                    prop.IsPositionFrozen = false;
                    prop.IsCollisionEnabled = true;
                    prop.HasGravity = true;
                    _props.Add(prop);
                    spawnedCount++;
                }
            }
            catch (Exception exception)
            {
                error =
                    "Pile spawning stopped after " + spawnedCount +
                    " props: " + exception.Message;
                Clear();
                return false;
            }
            finally
            {
                model.MarkAsNoLongerNeeded();
            }

            if (spawnedCount != PropCount)
            {
                Clear();
                return false;
            }

            return true;
        }

        public void Clear()
        {
            for (int index = _props.Count - 1; index >= 0; index--)
            {
                Prop prop = _props[index];

                try
                {
                    if (prop != null && prop.Exists())
                    {
                        prop.Delete();
                    }
                }
                catch
                {
                    // Script shutdown can make entity natives unavailable.
                }
            }

            _props.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
