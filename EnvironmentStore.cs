using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;

namespace CAAI;

public sealed record EntityConnection(Guid Id, string Name, string Kind, string Notes,
    IReadOnlyList<EnvironmentEntity> Participants);

public sealed class EnvironmentStore
{
    private readonly ObservableCollection<EntityConnection> _connections = new();
    private readonly HashSet<EnvironmentEntity> _observed = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly HashSet<ObservableCollection<EnvironmentEntity>> _collections = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    private readonly HashSet<Guid> _expandedData = new();

    public ObservableCollection<EnvironmentEntity> Entities { get; } = new();
    public ReadOnlyObservableCollection<EntityConnection> Connections { get; }
    public event EventHandler? Changed;

    public EnvironmentStore()
    {
        Connections = new ReadOnlyObservableCollection<EntityConnection>(_connections);
        Entities.CollectionChanged += OnEntitiesChanged;
    }

    public IReadOnlyList<EnvironmentEntity> GetAllEntities()
    {
        var result = new List<EnvironmentEntity>();
        var visited = new HashSet<EnvironmentEntity>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        void Visit(EnvironmentEntity entity)
        {
            if (!visited.Add(entity)) return;
            result.Add(entity);
            foreach (var child in entity.Children) Visit(child);
        }
        foreach (var entity in Entities) Visit(entity);
        return result;
    }

    public EntityConnection Connect(string name, IEnumerable<EnvironmentEntity> participants,
        string kind = "Related", string notes = "")
    {
        var connection = Create(Guid.NewGuid(), name, kind, notes, participants);
        _connections.Add(connection);
        Changed?.Invoke(this, EventArgs.Empty);
        return connection;
    }

    public EntityConnection Update(Guid id, string name, IEnumerable<EnvironmentEntity> participants,
        string kind = "Related", string notes = "")
    {
        var index = _connections.ToList().FindIndex(connection => connection.Id == id);
        if (index < 0) throw new ArgumentException("Connection not found.", nameof(id));
        var updated = Create(id, name, kind, notes, participants);
        _connections[index] = updated;
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public bool RemoveConnection(Guid id)
    {
        var connection = _connections.FirstOrDefault(candidate => candidate.Id == id);
        if (connection == null) return false;
        _connections.Remove(connection);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<EntityConnection> GetConnections(EnvironmentEntity entity) =>
        _connections.Where(connection => connection.Participants.Contains(entity)).ToArray();

    public string GetPath(EnvironmentEntity target)
    {
        var visited = new HashSet<EnvironmentEntity>();
        string? Find(EnvironmentEntity entity, string path)
        {
            if (!visited.Add(entity)) return null;
            if (ReferenceEquals(entity, target)) return path;
            foreach (var child in entity.Children)
                if (Find(child, $"{path}.{child.Name}") is { } found) return found;
            return null;
        }
        foreach (var entity in Entities)
            if (Find(entity, entity.Name) is { } path) return path;
        return target.Name;
    }

    public void AddDataMembers(EnvironmentEntity entity)
    {
        if (entity.Type != EntityType.DataValue || !GetAllEntities().Contains(entity))
            throw new ArgumentException("Select a data entity in this environment.", nameof(entity));
        if (entity.Value == null || !_expandedData.Add(entity.Id)) return;
        void Add(string name, object? value, string description) => entity.Children.Add(new EnvironmentEntity
        {
            Name = name, Type = EntityType.DataValue, Value = value, Description = description
        });
        var value = entity.Value;
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object)
                foreach (var property in json.EnumerateObject().Take(100)) Add(property.Name, property.Value.Clone(), "JSON property");
            else if (json.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in json.EnumerateArray().Take(100)) Add($"[{index++}]", item.Clone(), "JSON item");
            }
            return;
        }
        var node = new ObjectNode(entity.Name, value);
        node.LoadChildren();
        foreach (var child in (node.Children ?? []).Where(child => !child.IsGroup).Take(100))
            Add(child.Name, child.RawValue, child.IsDeferred ? $"{child.Type} property (not evaluated)" : $"{child.Kind}: {child.Type}");
    }

    public bool RemoveEntity(EnvironmentEntity entity)
    {
        if (Entities.Remove(entity)) return true;
        foreach (var parent in GetAllEntities())
            if (parent.Children.Remove(entity)) return true;
        return false;
    }

    private EntityConnection Create(Guid id, string name, string kind, string notes, IEnumerable<EnvironmentEntity> participants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(participants);
        var entities = participants.Distinct().ToArray();
        if (entities.Length < 2) throw new ArgumentException("Select at least two different entities.", nameof(participants));
        var available = GetAllEntities().ToHashSet();
        if (entities.Any(entity => !available.Contains(entity)))
            throw new ArgumentException("Every participant must belong to this environment.", nameof(participants));
        return new EntityConnection(id, name.Trim(), kind.Trim(), notes.Trim(), Array.AsReadOnly(entities));
    }

    private void OnEntitiesChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshSubscriptions();

    private void OnEntityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(EnvironmentEntity.IsSelected) or nameof(EnvironmentEntity.IsExpanded)) return;
        if (args.PropertyName == nameof(EnvironmentEntity.Value) && sender is EnvironmentEntity entity && _expandedData.Remove(entity.Id))
        {
            entity.Children.Clear();
            return;
        }
        if (args.PropertyName == nameof(EnvironmentEntity.Children)) RefreshSubscriptions();
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshSubscriptions()
    {
        foreach (var entity in _observed) entity.PropertyChanged -= OnEntityChanged;
        foreach (var collection in _collections) collection.CollectionChanged -= OnEntitiesChanged;
        _observed.Clear();
        _collections.Clear();
        foreach (var entity in GetAllEntities())
        {
            _observed.Add(entity);
            entity.PropertyChanged += OnEntityChanged;
            if (!ReferenceEquals(entity.Children, Entities) && _collections.Add(entity.Children))
                entity.Children.CollectionChanged += OnEntitiesChanged;
        }
        foreach (var connection in _connections.Where(connection => connection.Participants.Any(entity => !_observed.Contains(entity))).ToArray())
            _connections.Remove(connection);
        _expandedData.IntersectWith(_observed.Select(entity => entity.Id));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}