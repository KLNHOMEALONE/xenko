﻿// Copyright (c) 2016 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SiliconStudio.Assets;
using SiliconStudio.Assets.Compiler;
using SiliconStudio.BuildEngine;
using SiliconStudio.Core.IO;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Core.Serialization;
using SiliconStudio.Core.Serialization.Contents;
using SiliconStudio.Core.Storage;
using SiliconStudio.Xenko.Assets.Entities;
using SiliconStudio.Xenko.Engine;
using SiliconStudio.Xenko.Graphics;
using SiliconStudio.Xenko.Graphics.GeometricPrimitives;
using SiliconStudio.Xenko.Physics;

namespace SiliconStudio.Xenko.Assets.Navigation
{
    class NavigationMeshAssetCompiler : AssetCompilerBase
    {
        protected override void Compile(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var asset = (NavigationMeshAsset)assetItem.Asset;
            result.BuildSteps = new AssetBuildStep(assetItem) { new NavmeshBuildCommand(targetUrlInStorage, assetItem, asset, context) };
        }

        private class NavmeshBuildCommand : AssetCommand<NavigationMeshAsset>
        {
            // Deferred shapes such as infinite planes which should be added after the bounding box of the scene is generated
            private struct DeferredShape
            {
                public Matrix Transform;
                public IColliderShapeDesc Description;
                public Entity Entity;
                public NavigationMeshInputBuilder NavigationMeshInputBuilder;
            }

            private NavigationMeshCachedBuild oldBuild;
            private NavigationMeshCachedBuild currentBuild;

            private UFile assetUrl;
            private NavigationMeshAsset asset;
            private readonly Package package;

            // Combined scene data to create input meshData
            private NavigationMeshInputBuilder sceneNavigationMeshInputBuilder;
            private BoundingBox globalBoundingBox;
            private bool generateBoundingBox;

            private List<BoundingBox> updatedAreas = new List<BoundingBox>();
            private bool fullRebuild = false;

            // Automatically calculated bounding box
            private NavigationMeshBuildSettings buildSettings;

            private List<DeferredShape> deferredShapes = new List<DeferredShape>();

            public NavmeshBuildCommand(string url, AssetItem assetItem, NavigationMeshAsset value, AssetCompilerContext context)
                : base(url, value)
            {
                asset = value;
                package = assetItem.Package;
                assetUrl = url;
            }

            protected override void ComputeParameterHash(BinarySerializationWriter writer)
            {
                base.ComputeParameterHash(writer);

                // Hash relevant scene objects
                if (asset.Scene != null)
                {
                    string sceneUrl = AttachedReferenceManager.GetUrl(asset.Scene);
                    var sceneAsset = (SceneAsset)package.Session.FindAsset(sceneUrl)?.Asset;

                    // Clone scene asset because we update the world transformation matrices
                    var clonedSceneAsset = (SceneAsset)AssetCloner.Clone(sceneAsset);

                    // Turn the entire entity hierarchy into a single list
                    List<Entity> sceneEntities = clonedSceneAsset.Hierarchy.Parts.Select(x => x.Entity).ToList();

                    int sceneHash = CollectInputHash(sceneEntities);
                    writer.Write(sceneHash);
                }
            }

            protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
            {
                var intermediateDataId = ComputeAssetIntermediateDataId();

                // Build cache items to build incrementally
                currentBuild = new NavigationMeshCachedBuild();
                oldBuild = LoadIntermediateData(intermediateDataId);

                // The output object of the compilation
                NavigationMesh generatedNavigationMesh = new NavigationMesh();

                // No scene specified, result in failure
                if (asset.Scene == null)
                    return Task.FromResult(ResultStatus.Failed);

                if (asset.AutoGenerateBoundingBox)
                {
                    generateBoundingBox = true;
                    globalBoundingBox = BoundingBox.Empty;
                }
                else
                {
                    generateBoundingBox = false;
                    globalBoundingBox = asset.BoundingBox;
                }

                // Copy build settings so we can modify them
                buildSettings = asset.BuildSettings;

                // Check for tile size
                if (buildSettings.TileSize <= 0)
                    return Task.FromResult(ResultStatus.Failed);

                var assetManager = new ContentManager();
                string sceneUrl = AttachedReferenceManager.GetUrl(asset.Scene);
                var sceneAsset = (SceneAsset)package.Session.FindAsset(sceneUrl)?.Asset;

                if (sceneAsset == null)
                    return Task.FromResult(ResultStatus.Failed);

                // Clone scene asset because we update the world transformation matrices
                var clonedSceneAsset = (SceneAsset)AssetCloner.Clone(sceneAsset);

                // Turn the entire entity hierarchy into a single list
                List<Entity> sceneEntities = clonedSceneAsset.Hierarchy.Parts.Select(x => x.Entity).ToList();

                // This collects all the input geometry, calculates the modified areas and calculates the scene bounds
                CollectInputGeometry(sceneEntities);
                List<BoundingBox> removedAreas = oldBuild?.GetRemovedAreas(sceneEntities);

                BoundingBox boundingBox = globalBoundingBox;
                // Can't generate when no bounding box or and invalid bounding box is specified
                // this means that either the user specified bounding box is wrong or the scene does not contain any colliders
                if (boundingBox.Extent.X > 0 && boundingBox.Extent.Y > 0 && boundingBox.Extent.Z > 0)
                {
                    // Turn generated data into arrays
                    Vector3[] meshVertices = sceneNavigationMeshInputBuilder.Points.ToArray();
                    int[] meshIndices = sceneNavigationMeshInputBuilder.Indices.ToArray();

                    // NOTE: Reversed winding order as input to recast
                    int numSrcTriangles = meshIndices.Length/3;
                    for (int i = 0; i < numSrcTriangles; i++)
                    {
                        int j = meshIndices[i*3 + 1];
                        meshIndices[i*3 + 1] = meshIndices[i*3 + 2];
                        meshIndices[i*3 + 2] = j;
                    }

                    // Check if settings changed to trigger a full rebuild
                    int currentSettingsHash = asset.GetHashCode();
                    currentBuild.SettingsHash = currentSettingsHash;
                    if (oldBuild != null && oldBuild.SettingsHash != currentBuild.SettingsHash)
                    {
                        fullRebuild = true;
                    }

                    if (oldBuild != null && !fullRebuild)
                    {
                        // Perform incremental build on old navigation mesh
                        generatedNavigationMesh = oldBuild.NavigationMesh;
                    }

                    // Initialize navigation mesh for building
                    generatedNavigationMesh.Initialize(buildSettings, asset.NavigationMeshAgentSettings.ToArray());

                    // Set the new navigation mesh in the current build
                    currentBuild.NavigationMesh = generatedNavigationMesh;

                    // Generate all the layers corresponding to the various agent settings
                    for (int layer = 0; layer < asset.NavigationMeshAgentSettings.Count; layer++)
                    {
                        Stopwatch layerBuildTimer = new Stopwatch();
                        layerBuildTimer.Start();
                        var agentSetting = asset.NavigationMeshAgentSettings[layer];

                        // Flag tiles to build for this specific layer
                        HashSet<Point> tilesToBuild = new HashSet<Point>();
                        if (fullRebuild)
                        {
                            // For full rebuild just take the root bounding box for selecting tiles to build
                            List<Point> newTileList = NavigationMeshBuildUtils.GetOverlappingTiles(buildSettings, boundingBox);
                            foreach (Point p in newTileList)
                                tilesToBuild.Add(p);
                        }
                        else
                        {
                            // Apply an offset so their neighbouring tiles which are affected by the agent radius also get rebuild
                            Vector3 agentOffset = new Vector3(agentSetting.Radius, 0, agentSetting.Radius);
                            if (removedAreas != null)
                                updatedAreas.AddRange(removedAreas);
                            foreach (var update in updatedAreas)
                            {
                                BoundingBox agentSpecificBoundingBox = new BoundingBox
                                {
                                    Minimum = update.Minimum - agentOffset,
                                    Maximum = update.Maximum + agentOffset,
                                };
                                List<Point> newTileList = NavigationMeshBuildUtils.GetOverlappingTiles(buildSettings, agentSpecificBoundingBox);
                                foreach (Point p in newTileList)
                                    tilesToBuild.Add(p);
                            }
                        }

                        // Build tiles
                        foreach (var tileToBuild in tilesToBuild)
                        {
                            BoundingBox tileBoundingBox = NavigationMeshBuildUtils.ClampBoundingBoxToTile(buildSettings, boundingBox, tileToBuild);
                            // Check if tile bounding box is contained withing the navigation mesh bounding box
                            if (boundingBox.Contains(ref tileBoundingBox) == ContainmentType.Disjoint)
                            {
                                // Remove this tile
                                generatedNavigationMesh.Layers[layer].RemoveLayerTile(tileToBuild);
                                continue;
                            }

                            // Build the tile for the current layer being processed
                            generatedNavigationMesh.Layers[layer].BuildTile(meshVertices.ToArray(), meshIndices.ToArray(), tileBoundingBox, tileToBuild);
                        }
                    }
                }

                // Store used bounding box in navigation mesh
                generatedNavigationMesh.BoundingBox = boundingBox;

                assetManager.Save(assetUrl, generatedNavigationMesh);
                SaveIntermediateData(intermediateDataId, currentBuild);

                return Task.FromResult(ResultStatus.Successful);
            }

            /// <summary>
            /// Computes a unique Id for this asset used to store intermediate / build cache data
            /// </summary>
            /// <returns>The object id for asset intermediate data</returns>
            private ObjectId ComputeAssetIntermediateDataId()
            {
                var stream = new DigestStream(Stream.Null);
                var writer = new BinarySerializationWriter(stream);
                writer.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
                writer.Write(CommandCacheVersion);

                // Write binary format version
                writer.Write(DataSerializer.BinaryFormatVersion);

                // Compute assembly hash
                ComputeAssemblyHash(writer);

                // Write asset Id
                writer.Write(asset.Id);

                return stream.CurrentHash;
            }

            /// <summary>
            /// Loads intermediate data used for building a navigation mesh
            /// </summary>
            /// <param name="objectId">The unique Id for this data in the object database</param>
            /// <returns>The found cached build or null if there is no previous build</returns>
            private NavigationMeshCachedBuild LoadIntermediateData(ObjectId objectId)
            {
                try
                {
                    var objectDatabase = ContentManager.FileProvider.ObjectDatabase;
                    using (var stream = objectDatabase.OpenStream(objectId))
                    {
                        var reader = new BinarySerializationReader(stream);
                        NavigationMeshCachedBuild result = new NavigationMeshCachedBuild();
                        reader.Serialize(ref result, ArchiveMode.Deserialize);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    return null;
                }
            }

            /// <summary>
            /// Saves intermediate data used for building a navigation mesh
            /// </summary>
            /// <param name="objectId">The unique Id for this data in the object database</param>
            /// <param name="build">The build data to save</param>
            private void SaveIntermediateData(ObjectId objectId, NavigationMeshCachedBuild build)
            {
                var objectDatabase = ContentManager.FileProvider.ObjectDatabase;
                using (var stream = objectDatabase.OpenStream(objectId, VirtualFileMode.Create, VirtualFileAccess.Write))
                {
                    var writer = new BinarySerializationWriter(stream);
                    writer.Serialize(ref build, ArchiveMode.Serialize);
                    writer.Flush();
                }
            }

            private int CollectInputHash(List<Entity> sceneEntities)
            {
                int hash = 0;
                foreach (var entity in sceneEntities)
                {
                    StaticColliderComponent collider = entity.Get<StaticColliderComponent>();

                    bool colliderEnabled = collider != null && ((CollisionFilterGroupFlags)collider.CollisionGroup & asset.IncludedCollisionGroups) != 0 && collider.Enabled;
                    if (colliderEnabled) // Removed or disabled
                    {
                        // Update world transform before hashing
                        entity.Transform.UpdateWorldMatrix();

                        hash += NavigationMeshBuildUtils.HashEntityCollider(collider);
                    }
                }
                return hash;
            }

            private void CollectInputGeometry(List<Entity> sceneEntities)
            {
                // Reset state
                fullRebuild = false;
                updatedAreas.Clear();
                fullRebuild = oldBuild == null;

                sceneNavigationMeshInputBuilder = new NavigationMeshInputBuilder();

                foreach (var entity in sceneEntities)
                {
                    StaticColliderComponent collider = entity.Get<StaticColliderComponent>();

                    bool colliderEnabled = collider != null && ((CollisionFilterGroupFlags)collider.CollisionGroup & asset.IncludedCollisionGroups) != 0 && collider.Enabled;
                    if (colliderEnabled)
                        entity.Transform.UpdateWorldMatrix(); // Update world transform so the update check uses the current world transform

                    if (!colliderEnabled) // Removed or disabled
                    {
                        // Check for old object
                        NavigationMeshCachedBuildObject oldObject = null;
                        if (oldBuild?.Objects.TryGetValue(entity.Id, out oldObject) ?? false)
                        {
                            // This object has been disabled, update the area because it was removed and continue to the next object
                            updatedAreas.Add(oldObject.Data.BoundingBox);
                        }
                    }
                    else if (oldBuild?.IsUpdatedOrNew(entity) ?? true) // Updated?
                    {
                        TransformComponent entityTransform = entity.Transform;
                        Matrix entityWorldMatrix = entityTransform.WorldMatrix;

                        NavigationMeshInputBuilder entityNavigationMeshInputBuilder = new NavigationMeshInputBuilder();

                        collider.ComposeShape();
                        if (collider.ColliderShape == null)
                            continue; // No collider

                        bool isDeferred = false;

                        // Interate through all the colliders shapes while queueing all shapes in compound shapes to process those as well
                        Queue<ColliderShape> shapesToProcess = new Queue<ColliderShape>();
                        shapesToProcess.Enqueue(collider.ColliderShape);
                        while (shapesToProcess.Count > 0)
                        {
                            var shape = shapesToProcess.Dequeue();
                            var shapeType = shape.GetType();
                            if (shapeType == typeof(BoxColliderShape))
                            {
                                var box = (BoxColliderShape)shape;
                                var boxDesc = (BoxColliderShapeDesc)box.Description;
                                Matrix transform = box.PositiveCenterMatrix*entityWorldMatrix;

                                var meshData = GeometricPrimitive.Cube.New(boxDesc.Size);
                                entityNavigationMeshInputBuilder.AppendMeshData(meshData, transform);
                            }
                            else if (shapeType == typeof(SphereColliderShape))
                            {
                                var sphere = (SphereColliderShape)shape;
                                var sphereDesc = (SphereColliderShapeDesc)sphere.Description;
                                Matrix transform = sphere.PositiveCenterMatrix*entityWorldMatrix;

                                var meshData = GeometricPrimitive.Sphere.New(sphereDesc.Radius);
                                entityNavigationMeshInputBuilder.AppendMeshData(meshData, transform);
                            }
                            else if (shapeType == typeof(CylinderColliderShape))
                            {
                                var cylinder = (CylinderColliderShape)shape;
                                var cylinderDesc = (CylinderColliderShapeDesc)cylinder.Description;
                                Matrix transform = cylinder.PositiveCenterMatrix*entityWorldMatrix;

                                var meshData = GeometricPrimitive.Cylinder.New(cylinderDesc.Height, cylinderDesc.Radius);
                                entityNavigationMeshInputBuilder.AppendMeshData(meshData, transform);
                            }
                            else if (shapeType == typeof(CapsuleColliderShape))
                            {
                                var capsule = (CapsuleColliderShape)shape;
                                var capsuleDesc = (CapsuleColliderShapeDesc)capsule.Description;
                                Matrix transform = capsule.PositiveCenterMatrix*entityWorldMatrix;

                                var meshData = GeometricPrimitive.Capsule.New(capsuleDesc.Length, capsuleDesc.Radius);
                                entityNavigationMeshInputBuilder.AppendMeshData(meshData, transform);
                            }
                            else if (shapeType == typeof(ConeColliderShape))
                            {
                                var cone = (ConeColliderShape)shape;
                                var coneDesc = (ConeColliderShapeDesc)cone.Description;
                                Matrix transform = cone.PositiveCenterMatrix*entityWorldMatrix;

                                var meshData = GeometricPrimitive.Cone.New(coneDesc.Radius, coneDesc.Height);
                                entityNavigationMeshInputBuilder.AppendMeshData(meshData, transform);
                            }
                            else if (shapeType == typeof(StaticPlaneColliderShape))
                            {
                                var plane = (StaticPlaneColliderShape)shape;
                                var planeDesc = (StaticPlaneColliderShapeDesc)plane.Description;
                                Matrix transform = plane.PositiveCenterMatrix*entityWorldMatrix;

                                // Defer infinite planes because their size is not defined yet
                                deferredShapes.Add(new DeferredShape
                                {
                                    Description = planeDesc,
                                    Transform = transform,
                                    Entity = entity,
                                    NavigationMeshInputBuilder = entityNavigationMeshInputBuilder
                                });
                                isDeferred = true;
                            }
                            else if (shapeType == typeof(ConvexHullColliderShape))
                            {
                                // TODO: Fix loading of hull assets
                                var hull = (ConvexHullColliderShape)shape;
                                var hullDesc = (ConvexHullColliderShapeDesc)hull.Description;
                                Matrix transform = hull.PositiveCenterMatrix*entityWorldMatrix;
                            }
                            else if (shapeType == typeof(CompoundColliderShape))
                            {
                                // Unroll compound collider shapes
                                var compound = (CompoundColliderShape)shape;
                                for (int i = 0; i < compound.Count; i++)
                                {
                                    shapesToProcess.Enqueue(compound[i]);
                                }
                            }
                        }

                        if (!isDeferred)
                        {
                            // Store current entity in build cache
                            currentBuild.Add(entity, entityNavigationMeshInputBuilder);

                            // Add (?old) and new bounding box to modified areas
                            sceneNavigationMeshInputBuilder.AppendOther(entityNavigationMeshInputBuilder);
                            NavigationMeshCachedBuildObject oldObject = null;
                            if (oldBuild?.Objects.TryGetValue(entity.Id, out oldObject) ?? false)
                            {
                                updatedAreas.Add(oldObject.Data.BoundingBox);
                            }
                            updatedAreas.Add(entityNavigationMeshInputBuilder.BoundingBox);
                        }
                    }
                    else // Not updated
                    {
                        // Copy old data into vertex buffer
                        NavigationMeshCachedBuildObject oldObject = oldBuild.Objects[entity.Id];
                        sceneNavigationMeshInputBuilder.AppendOther(oldObject.Data);
                        currentBuild.Add(entity, oldObject.Data);
                    }
                }

                // Store calculated bounding box
                if (generateBoundingBox)
                    globalBoundingBox = sceneNavigationMeshInputBuilder.BoundingBox;

                // Process deferred shapes
                Vector3 maxSize = globalBoundingBox.Maximum - globalBoundingBox.Minimum;
                float maxDiagonal = Math.Max(maxSize.X, Math.Max(maxSize.Y, maxSize.Z));
                foreach (DeferredShape shape in deferredShapes)
                {
                    StaticPlaneColliderShapeDesc planeDesc = (StaticPlaneColliderShapeDesc)shape.Description;
                    Plane plane = new Plane(planeDesc.Normal, planeDesc.Offset);

                    // Pre-Transform plane parameters
                    plane.Normal = Vector3.TransformNormal(plane.Normal, shape.Transform);
                    float offset = Vector3.Dot(shape.Transform.TranslationVector, plane.Normal);
                    plane.D += offset;

                    // Generate source plane triangles
                    Vector3[] planePoints;
                    int[] planeInds;
                    NavigationMeshBuildUtils.BuildPlanePoints(ref plane, maxDiagonal, out planePoints, out planeInds);

                    Vector3 tangent, bitangent;
                    NavigationMeshBuildUtils.GenerateTangentBinormal(plane.Normal, out tangent, out bitangent);
                    // Calculate plane offset so that the plane always covers the whole range of the bounding box
                    Vector3 planeOffset = Vector3.Dot(globalBoundingBox.Center, tangent)*tangent;
                    planeOffset += Vector3.Dot(globalBoundingBox.Center, bitangent)*bitangent;

                    VertexPositionNormalTexture[] vertices = new VertexPositionNormalTexture[planePoints.Length];
                    for (int i = 0; i < planePoints.Length; i++)
                    {
                        vertices[i] = new VertexPositionNormalTexture(planePoints[i] + planeOffset, Vector3.UnitY, Vector2.Zero);
                    }

                    GeometricMeshData<VertexPositionNormalTexture> meshData = new GeometricMeshData<VertexPositionNormalTexture>(vertices, planeInds, false);
                    shape.NavigationMeshInputBuilder.AppendMeshData(meshData, Matrix.Identity);
                    sceneNavigationMeshInputBuilder.AppendMeshData(meshData, Matrix.Identity);
                    // Update bounding box after plane generation
                    if (generateBoundingBox)
                        globalBoundingBox = sceneNavigationMeshInputBuilder.BoundingBox;

                    // Store deferred shape in build cahce just like normal onesdddd
                    currentBuild.Add(shape.Entity, shape.NavigationMeshInputBuilder);

                    // NOTE: Force a full rebuild when moving unbound shapes such as ininite planes
                    // the alternative is to intersect the old and new plane with the tiles to see which ones changed
                    // although in most cases it will be a horizontal plane and all tiles will be affected anyways
                    fullRebuild = true;
                }
            }
        }
    }
}