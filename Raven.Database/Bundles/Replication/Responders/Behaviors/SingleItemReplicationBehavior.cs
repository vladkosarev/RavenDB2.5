using System.Diagnostics;
using System.Linq;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Bundles.Replication.Triggers;
using Raven.Database;
using Raven.Database.Bundles.Replication.Impl;
using Raven.Database.Storage;
using Raven.Json.Linq;

namespace Raven.Bundles.Replication.Responders
{
	public abstract class SingleItemReplicationBehavior<TInternal, TExternal>
	{
		protected class CreatedConflict
		{
			public Etag Etag { get; set; }
			public string[] ConflictedIds { get; set; }
		}

		private readonly ILog log = LogManager.GetCurrentClassLogger();

		public DocumentDatabase Database { get; set; }
		public IStorageActionsAccessor Actions { get; set; }
		public string Src { get; set; }

		public void Replicate(string id, RavenJObject metadata, TExternal incoming)
		{
			if (metadata.Value<bool>(Constants.RavenDeleteMarker))
			{
				ReplicateDelete(id, metadata, incoming);
				return;
			}
			TInternal existingItem;
			Etag existingEtag;
			bool deleted;
			var existingMetadata = TryGetExisting(id, out existingItem, out existingEtag, out deleted);
			if (existingMetadata == null)
			{
				AddWithoutConflict(id, null, metadata, incoming);
				log.Debug("New item {0} replicated successfully from {1}", id, Src);
				return;
			}


			// we just got the same version from the same source - request playback again?
			// at any rate, not an error, moving on
			if (existingMetadata.Value<string>(Constants.RavenReplicationSource) == metadata.Value<string>(Constants.RavenReplicationSource)
				&& existingMetadata.Value<long>(Constants.RavenReplicationVersion) == metadata.Value<long>(Constants.RavenReplicationVersion))
			{
				return;
			}


			var existingDocumentIsInConflict = existingMetadata[Constants.RavenReplicationConflict] != null;

			if (existingDocumentIsInConflict == false &&                    // if the current document is not in conflict, we can continue without having to keep conflict semantics
				(Historian.IsDirectChildOfCurrent(metadata, existingMetadata)))		// this update is direct child of the existing doc, so we are fine with overwriting this
			{
				var etag = deleted == false ? existingEtag : null;
				AddWithoutConflict(id, etag, metadata, incoming);

				log.Debug("Existing item {0} replicated successfully from {1}", id, Src);
				return;
			}

			if (TryResolveConflict(id, metadata, incoming, existingItem))
			{
                if (metadata.ContainsKey("Raven-Remove-Document-Marker") &&
                   metadata.Value<bool>("Raven-Remove-Document-Marker"))
                {
                    DeleteItem(id, null);
                    MarkAsDeleted(id, metadata);
                }
                else
                {
                    var etag = deleted == false ? existingEtag : null;

					var resolvedItemJObject = incoming as RavenJObject;
					if (resolvedItemJObject != null)
						ExecuteRemoveConflictOnPutTrigger(id, metadata, resolvedItemJObject);

                    AddWithoutConflict(id, etag, metadata, incoming);
                }
                return;
			}

			CreatedConflict createdConflict;

			var newDocumentConflictId = SaveConflictedItem(id, metadata, incoming, existingEtag);

			if (existingDocumentIsInConflict) // the existing document is in conflict
			{
				log.Debug("Conflicted item {0} has a new version from {1}, adding to conflicted documents", id, Src);

				createdConflict = AppendToCurrentItemConflicts(id, newDocumentConflictId, existingMetadata, existingItem);
			}
			else
			{
				log.Debug("Existing item {0} is in conflict with replicated version from {1}, marking item as conflicted", id, Src);

				// we have a new conflict
				// move the existing doc to a conflict and create a conflict document
				var existingDocumentConflictId = id + "/conflicts/" + GetReplicationIdentifierForCurrentDatabase();

				createdConflict = CreateConflict(id, newDocumentConflictId, existingDocumentConflictId, existingItem,
												  existingMetadata);
			}

			Database.TransactionalStorage.ExecuteImmediatelyOrRegisterForSynchronization(() =>
																	Database.RaiseNotifications(new ReplicationConflictNotification()
																	{
																		Id = id,
																		Etag = createdConflict.Etag,
																		ItemType = ReplicationConflict,
																		OperationType = ReplicationOperationTypes.Put,
																		Conflicts = createdConflict.ConflictedIds
																	}));
			}

		protected abstract ReplicationConflictTypes ReplicationConflict { get; }

		private string SaveConflictedItem(string id, RavenJObject metadata, TExternal incoming, Etag existingEtag)
		{
			metadata[Constants.RavenReplicationConflictDocument] = true;
			var newDocumentConflictId = id + "/conflicts/" + GetReplicationIdentifier(metadata);
			metadata.Add(Constants.RavenReplicationConflict, RavenJToken.FromObject(true));
			AddWithoutConflict(
				newDocumentConflictId,
				null, // we explicitly want to overwrite a document if it already exists, since it  is known uniuque by the key 
				metadata, 
				incoming);
			return newDocumentConflictId;
		}

		private void ReplicateDelete(string id, RavenJObject newMetadata, TExternal incoming)
		{
			TInternal existingItem;
			Etag existingEtag;
			bool deleted;
			var existingMetadata = TryGetExisting(id, out existingItem, out existingEtag, out deleted);
			if (existingMetadata == null)
			{
				log.Debug("Replicating deleted item {0} from {1} that does not exist, ignoring", id, Src);
				return;
			}

			// we just got the same version from the same source - request playback again?
			// at any rate, not an error, moving on
			if (existingMetadata.Value<string>(Constants.RavenReplicationSource) ==
				newMetadata.Value<string>(Constants.RavenReplicationSource)
				&&
				existingMetadata.Value<long>(Constants.RavenReplicationVersion) ==
				newMetadata.Value<long>(Constants.RavenReplicationVersion))
			{
				return;
			}

			if (existingMetadata.Value<bool>(Constants.RavenDeleteMarker)) //deleted locally as well
			{
				log.Debug("Replicating deleted item {0} from {1} that was deleted locally. Merging histories", id, Src);
				var existingHistory = new RavenJArray(ReplicationData.GetHistory(existingMetadata));
				var newHistory = new RavenJArray(ReplicationData.GetHistory(newMetadata));

				foreach (var item in newHistory)
				{
					if (existingHistory.Contains(item, RavenJTokenEqualityComparer.Default))
						continue;

					existingHistory.Add(item);
				}

				while (existingHistory.Length > Constants.ChangeHistoryLength)
				{
					existingHistory.RemoveAt(0);
				}

				MarkAsDeleted(id, newMetadata);
				return;
			}

			if (Historian.IsDirectChildOfCurrent(newMetadata, existingMetadata))// not modified
			{
				log.Debug("Delete of existing item {0} was replicated successfully from {1}", id, Src);
				DeleteItem(id, existingEtag);
				MarkAsDeleted(id, newMetadata);
				return;
			}

            if (TryResolveConflict(id, newMetadata, incoming, existingItem))
            {
                if (newMetadata.ContainsKey("Raven-Remove-Document-Marker") &&
                    newMetadata.Value<bool>("Raven-Remove-Document-Marker"))
                {
                    DeleteItem(id, null);
                    MarkAsDeleted(id, newMetadata);
                }
                else
                {
                    AddWithoutConflict(id, existingEtag, newMetadata, incoming);
                }
                return;
            }

			CreatedConflict createdConflict;

			if (existingMetadata.Value<bool>(Constants.RavenReplicationConflict)) // locally conflicted
			{
				log.Debug("Replicating deleted item {0} from {1} that is already conflicted, adding to conflicts.", id, Src);
				var savedConflictedItemId = SaveConflictedItem(id, newMetadata, incoming, existingEtag);
				createdConflict = AppendToCurrentItemConflicts(id, savedConflictedItemId, existingMetadata, existingItem);
			}
			else
			{
				var newConflictId = SaveConflictedItem(id, newMetadata, incoming, existingEtag);
				log.Debug("Existing item {0} is in conflict with replicated delete from {1}, marking item as conflicted", id, Src);

				// we have a new conflict  move the existing doc to a conflict and create a conflict document
				var existingDocumentConflictId = id + "/conflicts/" + GetReplicationIdentifierForCurrentDatabase();
				createdConflict = CreateConflict(id, newConflictId, existingDocumentConflictId, existingItem, existingMetadata);
			}

			Database.TransactionalStorage.ExecuteImmediatelyOrRegisterForSynchronization(() =>
														Database.RaiseNotifications(new ReplicationConflictNotification()
														{
															Id = id,
															Etag = createdConflict.Etag,
															Conflicts = createdConflict.ConflictedIds,
															ItemType = ReplicationConflictTypes.DocumentReplicationConflict,
															OperationType = ReplicationOperationTypes.Delete
														}));

			}


		private void ExecuteRemoveConflictOnPutTrigger(string id, RavenJObject metadata, RavenJObject resolvedItemJObject)
		{
			//since we are in replication handler, triggers are disabled, and if we are replicating PUT of conflict resolution,
			//we need to execute the relevant trigger manually
			// --> AddWithoutConflict() does PUT, but because of 'No Triggers' context the trigger itself is executed
			var removeConflictTrigger = Database.PutTriggers.GetAllParts()
				.Select(trg => trg.Value)
				.OfType<RemoveConflictOnPutTrigger>()
				.FirstOrDefault();

			Debug.Assert(removeConflictTrigger != null, "If this is null, this means something is very wrong - replication configured, and no relevant plugin is there.");
			removeConflictTrigger.OnPut(id, resolvedItemJObject, new RavenJObject(metadata), null);
		}

		protected abstract void DeleteItem(string id, Etag etag);

		protected abstract void MarkAsDeleted(string id, RavenJObject metadata);

		protected abstract void AddWithoutConflict(string id, Etag etag, RavenJObject metadata, TExternal incoming);

		protected abstract CreatedConflict CreateConflict(string id, string newDocumentConflictId, string existingDocumentConflictId, TInternal existingItem, RavenJObject existingMetadata);

		protected abstract CreatedConflict AppendToCurrentItemConflicts(string id, string newConflictId, RavenJObject existingMetadata, TInternal existingItem);

		protected abstract RavenJObject TryGetExisting(string id, out TInternal existingItem, out Etag existingEtag, out bool deleted);

		protected abstract bool TryResolveConflict(string id, RavenJObject metadata, TExternal document,
												  TInternal existing);


		private static string GetReplicationIdentifier(RavenJObject metadata)
		{
			return metadata.Value<string>(Constants.RavenReplicationSource);
		}

		private string GetReplicationIdentifierForCurrentDatabase()
		{
			return Database.TransactionalStorage.Id.ToString();
		}
	}
}
