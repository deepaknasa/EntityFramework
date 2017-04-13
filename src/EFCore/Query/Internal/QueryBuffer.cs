// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class QueryBuffer : IQueryBuffer, IDisposable
    {
        private readonly QueryContextDependencies _dependencies;

        private IWeakReferenceIdentityMap _identityMap0;
        private IWeakReferenceIdentityMap _identityMap1;
        private Dictionary<IKey, IWeakReferenceIdentityMap> _identityMaps;

        private readonly ConditionalWeakTable<object, object> _valueBuffers
            = new ConditionalWeakTable<object, object>();

        private readonly Dictionary<int, IEnumerator<object>> _includedCollections
            = new Dictionary<int, IEnumerator<object>>();

        private readonly Dictionary<int, IAsyncEnumerator<object>> _includedAsyncCollections
            = new Dictionary<int, IAsyncEnumerator<object>>();

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public QueryBuffer(
            [NotNull] QueryContextDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual object GetEntity(
            IKey key, EntityLoadInfo entityLoadInfo, bool queryStateManager, bool throwOnNullKey)
        {
            if (queryStateManager)
            {
                var entry = _dependencies.StateManager.TryGetEntry(key, entityLoadInfo.ValueBuffer, throwOnNullKey);

                if (entry != null)
                {
                    return entry.Entity;
                }
            }

            var identityMap = GetOrCreateIdentityMap(key);

            bool hasNullKey;
            var weakReference = identityMap.TryGetEntity(entityLoadInfo.ValueBuffer, throwOnNullKey, out hasNullKey);
            if (hasNullKey)
            {
                return null;
            }

            object entity;
            if (weakReference == null
                || !weakReference.TryGetTarget(out entity))
            {
                entity = entityLoadInfo.Materialize();

                if (weakReference != null)
                {
                    weakReference.SetTarget(entity);
                }
                else
                {
                    identityMap.CollectGarbage();

                    identityMap.Add(entityLoadInfo.ValueBuffer, entity);
                }

                _valueBuffers.Add(entity, entityLoadInfo.ForType(entity.GetType()));
            }

            return entity;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual object GetPropertyValue(object entity, IProperty property)
        {
            var entry = _dependencies.StateManager.TryGetEntry(entity);

            if (entry != null)
            {
                return entry[property];
            }

            object boxedValueBuffer;
            var found = _valueBuffers.TryGetValue(entity, out boxedValueBuffer);

            Debug.Assert(found);

            var valueBuffer = (ValueBuffer)boxedValueBuffer;

            return valueBuffer[property.GetIndex()];
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual void StartTracking(object entity, EntityTrackingInfo entityTrackingInfo)
        {
            object boxedValueBuffer;
            if (!_valueBuffers.TryGetValue(entity, out boxedValueBuffer))
            {
                boxedValueBuffer = ValueBuffer.Empty;
            }

            entityTrackingInfo
                .StartTracking(_dependencies.StateManager, entity, (ValueBuffer)boxedValueBuffer);

            foreach (var includedEntity
                in entityTrackingInfo.GetIncludedEntities(_dependencies.StateManager, entity)
                    .Where(includedEntity
                        => _valueBuffers.TryGetValue(includedEntity.Entity, out boxedValueBuffer)))
            {
                includedEntity.StartTracking(_dependencies.StateManager, (ValueBuffer)boxedValueBuffer);
            }
        }

        /// <summary>
         ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
         ///     directly from your code. This API may change or be removed in future releases.
         /// </summary>
        public virtual void StartTracking(object entity, IEntityType entityType)
        {
            object boxedValueBuffer;
            if (!_valueBuffers.TryGetValue(entity, out boxedValueBuffer))
            {
                boxedValueBuffer = ValueBuffer.Empty;
            }

            _dependencies.StateManager
                .StartTrackingFromQuery(
                    entityType,
                    entity,
                    (ValueBuffer)boxedValueBuffer,
                    handledForeignKeys: null);
        }

        void IDisposable.Dispose()
        {
            foreach (var enumerator in _includedCollections.Values)
            {
                enumerator.Dispose();
            }

            foreach (var asyncEnumerator in _includedAsyncCollections.Values)
            {
                asyncEnumerator.Dispose();
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual void IncludeCollection(
            int includeId,
            INavigation navigation,
            INavigation inverseNavigation,
            IEntityType targetEntityType,
            IClrCollectionAccessor clrCollectionAccessor,
            IClrPropertySetter inverseClrPropertySetter,
            bool tracking,
            object entity,
            Func<IEnumerable<object>> relatedEntitiesFactory)
        {
            if (!_includedCollections.TryGetValue(includeId, out IEnumerator<object> enumerator))
            {
                enumerator = relatedEntitiesFactory().GetEnumerator();

                if (!enumerator.MoveNext())
                {
                    enumerator.Dispose();
                    enumerator = null;
                }

                _includedCollections.Add(includeId, enumerator);
            }

            if (enumerator == null)
            {
                clrCollectionAccessor.GetOrCreate(entity);

                return;
            }

            var relatedEntities = new List<object>();

            // TODO: This should be done at query compile time and not require a VB unless there are shadow props
            var keyComparer = CreateIncludeKeyComparer(entity, navigation);

            while (true)
            {
                bool shouldInclude;

                if (_valueBuffers.TryGetValue(enumerator.Current, out object relatedValueBuffer))
                {
                    shouldInclude = keyComparer.ShouldInclude((ValueBuffer)relatedValueBuffer);
                }
                else
                {
                    var entry = _dependencies.StateManager.TryGetEntry(enumerator.Current);

                    Debug.Assert(entry != null);

                    shouldInclude = keyComparer.ShouldInclude(entry);
                }

                if (shouldInclude)
                {
                    relatedEntities.Add(enumerator.Current);

                    if (tracking)
                    {
                        StartTracking(enumerator.Current, targetEntityType);
                    }

                    if (inverseNavigation != null)
                    {
                        Debug.Assert(inverseClrPropertySetter != null);

                        inverseClrPropertySetter.SetClrValue(enumerator.Current, entity);

                        if (tracking)
                        {
                            var internalEntityEntry = _dependencies.StateManager.TryGetEntry(enumerator.Current);

                            Debug.Assert(internalEntityEntry != null);

                            internalEntityEntry.SetRelationshipSnapshotValue(inverseNavigation, entity);
                        }
                    }

                    if (!enumerator.MoveNext())
                    {
                        enumerator.Dispose();

                        _includedCollections[includeId] = null;

                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            clrCollectionAccessor.AddRange(entity, relatedEntities);

            if (tracking)
            {
                var internalEntityEntry = _dependencies.StateManager.TryGetEntry(entity);

                Debug.Assert(internalEntityEntry != null);

                internalEntityEntry.AddRangeToCollectionSnapshot(navigation, relatedEntities);
                internalEntityEntry.SetIsLoaded(navigation);
            }
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual async Task IncludeCollectionAsync(
            int includeId,
            INavigation navigation,
            INavigation inverseNavigation,
            IEntityType targetEntityType,
            IClrCollectionAccessor clrCollectionAccessor,
            IClrPropertySetter inverseClrPropertySetter,
            bool tracking,
            object entity,
            Func<IAsyncEnumerable<object>> relatedEntitiesFactory,
            CancellationToken cancellationToken)
        {
            if (!_includedAsyncCollections.TryGetValue(includeId, out IAsyncEnumerator<object> asyncEnumerator))
            {
                asyncEnumerator = relatedEntitiesFactory().GetEnumerator();

                if (!await asyncEnumerator.MoveNext(cancellationToken))
                {
                    asyncEnumerator.Dispose();
                    asyncEnumerator = null;
                }

                _includedAsyncCollections.Add(includeId, asyncEnumerator);
            }

            if (asyncEnumerator == null)
            {
                clrCollectionAccessor.GetOrCreate(entity);

                return;
            }

            var relatedEntities = new List<object>();

            // TODO: This should be done at query compile time and not require a VB unless there are shadow props
            var keyComparer = CreateIncludeKeyComparer(entity, navigation);

            while (true)
            {
                bool shouldInclude;

                if (_valueBuffers.TryGetValue(asyncEnumerator.Current, out object relatedValueBuffer))
                {
                    shouldInclude = keyComparer.ShouldInclude((ValueBuffer)relatedValueBuffer);
                }
                else
                {
                    var entry = _dependencies.StateManager.TryGetEntry(asyncEnumerator.Current);

                    Debug.Assert(entry != null);

                    shouldInclude = keyComparer.ShouldInclude(entry);
                }

                if (shouldInclude)
                {
                    relatedEntities.Add(asyncEnumerator.Current);

                    if (tracking)
                    {
                        StartTracking(asyncEnumerator.Current, targetEntityType);
                    }

                    if (inverseNavigation != null)
                    {
                        Debug.Assert(inverseClrPropertySetter != null);

                        inverseClrPropertySetter.SetClrValue(asyncEnumerator.Current, entity);

                        if (tracking)
                        {
                            var internalEntityEntry = _dependencies.StateManager.TryGetEntry(asyncEnumerator.Current);

                            Debug.Assert(internalEntityEntry != null);

                            internalEntityEntry.SetRelationshipSnapshotValue(inverseNavigation, entity);
                        }
                    }

                    if (!await asyncEnumerator.MoveNext(cancellationToken))
                    {
                        asyncEnumerator.Dispose();

                        _includedAsyncCollections[includeId] = null;

                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            clrCollectionAccessor.AddRange(entity, relatedEntities);

            if (tracking)
            {
                var internalEntityEntry = _dependencies.StateManager.TryGetEntry(entity);

                Debug.Assert(internalEntityEntry != null);

                internalEntityEntry.AddRangeToCollectionSnapshot(navigation, relatedEntities);
                internalEntityEntry.SetIsLoaded(navigation);
            }
        }

        private IIncludeKeyComparer CreateIncludeKeyComparer(
            object entity,
            INavigation navigation)
        {
            var identityMap = GetOrCreateIdentityMap(navigation.ForeignKey.PrincipalKey);

            if (!_valueBuffers.TryGetValue(entity, out object boxedValueBuffer))
            {
                var entry = _dependencies.StateManager.TryGetEntry(entity);

                Debug.Assert(entry != null);

                return identityMap.CreateIncludeKeyComparer(navigation, entry);
            }

            return identityMap.CreateIncludeKeyComparer(navigation, (ValueBuffer)boxedValueBuffer);
        }

        #region Legacy Include

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual void Include(
            QueryContext queryContext,
            object entity,
            IReadOnlyList<INavigation> navigationPath,
            IReadOnlyList<IRelatedEntitiesLoader> relatedEntitiesLoaders,
            bool queryStateManager)
            => Include(
                queryContext,
                entity,
                navigationPath,
                relatedEntitiesLoaders,
                currentNavigationIndex: 0,
                queryStateManager: queryStateManager);

        private void Include(
            QueryContext queryContext,
            object entity,
            IReadOnlyList<INavigation> navigationPath,
            IReadOnlyList<IRelatedEntitiesLoader> relatedEntitiesLoaders,
            int currentNavigationIndex,
            bool queryStateManager)
        {
            if (entity == null
                || currentNavigationIndex == navigationPath.Count)
            {
                return;
            }

            var navigation = navigationPath[currentNavigationIndex];
            var keyComparer = IncludeCore(entity, navigation);
            var key = navigation.GetTargetType().FindPrimaryKey();

            LoadNavigationProperties(
                entity,
                navigationPath,
                currentNavigationIndex,
                relatedEntitiesLoaders[currentNavigationIndex]
                    .Load(queryContext, keyComparer)
                    .Select(eli =>
                        {
                            var targetEntity = GetEntity(key, eli, queryStateManager, throwOnNullKey: false);

                            Include(
                                queryContext,
                                targetEntity,
                                navigationPath,
                                relatedEntitiesLoaders,
                                currentNavigationIndex + 1,
                                queryStateManager);

                            return targetEntity;
                        })
                    .Where(e => e != null)
                    .ToList(),
                queryStateManager);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual Task IncludeAsync(
            QueryContext queryContext,
            object entity,
            IReadOnlyList<INavigation> navigationPath,
            IReadOnlyList<IAsyncRelatedEntitiesLoader> relatedEntitiesLoaders,
            bool queryStateManager,
            CancellationToken cancellationToken)
            => IncludeAsync(
                queryContext,
                entity,
                navigationPath,
                relatedEntitiesLoaders,
                currentNavigationIndex: 0,
                queryStateManager: queryStateManager,
                cancellationToken: cancellationToken);

        private async Task IncludeAsync(
            QueryContext queryContext,
            object entity,
            IReadOnlyList<INavigation> navigationPath,
            IReadOnlyList<IAsyncRelatedEntitiesLoader> relatedEntitiesLoaders,
            int currentNavigationIndex,
            bool queryStateManager,
            CancellationToken cancellationToken)
        {
            if (entity == null
                || currentNavigationIndex == navigationPath.Count)
            {
                return;
            }

            var navigation = navigationPath[currentNavigationIndex];
            var keyComparer = IncludeCore(entity, navigation);
            var key = navigation.GetTargetType().FindPrimaryKey();

            var relatedEntityLoadInfos
                = relatedEntitiesLoaders[currentNavigationIndex]
                    .Load(queryContext, keyComparer);

            var relatedObjects = new List<object>();

            using (var asyncEnumerator = relatedEntityLoadInfos.GetEnumerator())
            {
                while (await asyncEnumerator.MoveNext(cancellationToken))
                {
                    var targetEntity
                        = GetEntity(key, asyncEnumerator.Current, queryStateManager, throwOnNullKey: false);

                    if (targetEntity != null)
                    {
                        await IncludeAsync(
                            queryContext,
                            targetEntity,
                            navigationPath,
                            relatedEntitiesLoaders,
                            currentNavigationIndex + 1,
                            queryStateManager,
                            cancellationToken);

                        relatedObjects.Add(targetEntity);
                    }
                }
            }

            LoadNavigationProperties(
                entity,
                navigationPath,
                currentNavigationIndex,
                relatedObjects,
                queryStateManager);
        }

        private IIncludeKeyComparer IncludeCore(
            object entity,
            INavigation navigation)
        {
            var identityMap = GetOrCreateIdentityMap(navigation.ForeignKey.PrincipalKey);

            object boxedValueBuffer;
            if (!_valueBuffers.TryGetValue(entity, out boxedValueBuffer))
            {
                var entry = _dependencies.StateManager.TryGetEntry(entity);

                Debug.Assert(entry != null);

                return identityMap.CreateIncludeKeyComparer(navigation, entry);
            }

            return identityMap.CreateIncludeKeyComparer(navigation, (ValueBuffer)boxedValueBuffer);
        }

        private void LoadNavigationProperties(
            object entity,
            IReadOnlyList<INavigation> navigationPath,
            int currentNavigationIndex,
            IReadOnlyList<object> relatedEntities,
            bool tracking)
        {
            if (tracking)
            {
                _dependencies.ChangeDetector.Suspend();
            }

            try
            {
                var navigation = navigationPath[currentNavigationIndex];
                var inverseNavigation = navigation.FindInverse();

                if (navigation.IsDependentToPrincipal()
                    && relatedEntities.Any())
                {
                    var relatedEntity = relatedEntities[0];

                    SetNavigation(entity, navigation, relatedEntity, tracking);

                    if (inverseNavigation != null)
                    {
                        if (inverseNavigation.IsCollection())
                        {
                            AddToCollection(relatedEntity, inverseNavigation, entity, tracking);
                        }
                        else
                        {
                            SetNavigation(relatedEntity, inverseNavigation, entity, tracking);
                        }
                    }
                }
                else
                {
                    if (navigation.IsCollection())
                    {
                        AddRangeToCollection(entity, navigation, relatedEntities, tracking);

                        if (inverseNavigation != null)
                        {
                            var setter = inverseNavigation.GetSetter();

                            foreach (var relatedEntity in relatedEntities)
                            {
                                SetNavigation(relatedEntity, inverseNavigation, setter, entity, tracking);
                            }
                        }
                    }
                    else if (relatedEntities.Any())
                    {
                        var relatedEntity = relatedEntities[0];

                        SetNavigation(entity, navigation, relatedEntity, tracking);

                        if (inverseNavigation != null)
                        {
                            SetNavigation(relatedEntity, inverseNavigation, entity, tracking);
                        }
                    }
                }
            }
            finally
            {
                if (tracking)
                {
                    _dependencies.ChangeDetector.Resume();
                }
            }
        }

        private void SetNavigation(object entity, INavigation navigation, object value, bool tracking)
            => SetNavigation(entity, navigation, navigation.GetSetter(), value, tracking);

        private void SetNavigation(object entity, INavigation navigation, IClrPropertySetter setter, object value, bool tracking)
        {
            setter.SetClrValue(entity, value);

            if (tracking)
            {
                _dependencies.StateManager.TryGetEntry(entity)?.SetRelationshipSnapshotValue(navigation, value);
            }
        }

        private void AddToCollection(object entity, INavigation navigation, object value, bool tracking)
        {
            navigation.GetCollectionAccessor().Add(entity, value);

            if (tracking)
            {
                _dependencies.StateManager.TryGetEntry(entity)?.AddToCollectionSnapshot(navigation, value);
            }
        }

        private void AddRangeToCollection(object entity, INavigation navigation, IEnumerable<object> values, bool tracking)
        {
            navigation.GetCollectionAccessor().AddRange(entity, values);

            if (tracking)
            {
                _dependencies.StateManager.TryGetEntry(entity)?.AddRangeToCollectionSnapshot(navigation, values);
            }
        }

        private IWeakReferenceIdentityMap GetOrCreateIdentityMap(IKey key)
        {
            if (_identityMap0 == null)
            {
                _identityMap0 = key.GetWeakReferenceIdentityMapFactory()();
                return _identityMap0;
            }

            if (_identityMap0.Key == key)
            {
                return _identityMap0;
            }

            if (_identityMap1 == null)
            {
                _identityMap1 = key.GetWeakReferenceIdentityMapFactory()();
                return _identityMap1;
            }

            if (_identityMap1.Key == key)
            {
                return _identityMap1;
            }

            if (_identityMaps == null)
            {
                _identityMaps = new Dictionary<IKey, IWeakReferenceIdentityMap>();
            }

            IWeakReferenceIdentityMap identityMap;
            if (!_identityMaps.TryGetValue(key, out identityMap))
            {
                identityMap = key.GetWeakReferenceIdentityMapFactory()();
                _identityMaps[key] = identityMap;
            }
            return identityMap;
        }

        #endregion
    }
}
