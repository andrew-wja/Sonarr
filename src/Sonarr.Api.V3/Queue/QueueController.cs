using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.SignalR;
using Sonarr.Http;
using Sonarr.Http.Extensions;
using Sonarr.Http.REST;
using Sonarr.Http.REST.Attributes;
using Workarr.Blocklisting;
using Workarr.Datastore;
using Workarr.Datastore.Events;
using Workarr.Download;
using Workarr.Download.Pending;
using Workarr.Download.TrackedDownloads;
using Workarr.Extensions;
using Workarr.Indexers;
using Workarr.Languages;
using Workarr.Messaging.Events;
using Workarr.Profiles.Qualities;
using Workarr.Qualities;
using Workarr.Queue;

namespace Sonarr.Api.V3.Queue
{
    [V3ApiController]
    public class QueueController : RestControllerWithSignalR<QueueResource, Workarr.Queue.Queue>,
                               IHandle<QueueUpdatedEvent>, IHandle<PendingReleasesUpdatedEvent>
    {
        private readonly IQueueService _queueService;
        private readonly IPendingReleaseService _pendingReleaseService;

        private readonly QualityModelComparer _qualityComparer;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IIgnoredDownloadService _ignoredDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IBlocklistService _blocklistService;

        public QueueController(IBroadcastSignalRMessage broadcastSignalRMessage,
                           IQueueService queueService,
                           IPendingReleaseService pendingReleaseService,
                           IQualityProfileService qualityProfileService,
                           ITrackedDownloadService trackedDownloadService,
                           IFailedDownloadService failedDownloadService,
                           IIgnoredDownloadService ignoredDownloadService,
                           IProvideDownloadClient downloadClientProvider,
                           IBlocklistService blocklistService)
            : base(broadcastSignalRMessage)
        {
            _queueService = queueService;
            _pendingReleaseService = pendingReleaseService;
            _trackedDownloadService = trackedDownloadService;
            _failedDownloadService = failedDownloadService;
            _ignoredDownloadService = ignoredDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _blocklistService = blocklistService;

            _qualityComparer = new QualityModelComparer(qualityProfileService.GetDefaultProfile(string.Empty));
        }

        [NonAction]
        public override ActionResult<QueueResource> GetResourceByIdWithErrorHandler(int id)
        {
            return base.GetResourceByIdWithErrorHandler(id);
        }

        protected override QueueResource GetResourceById(int id)
        {
            throw new NotImplementedException();
        }

        [RestDeleteById]
        public void RemoveAction(int id, bool removeFromClient = true, bool blocklist = false, bool skipRedownload = false, bool changeCategory = false)
        {
            var pendingRelease = _pendingReleaseService.FindPendingQueueItem(id);

            if (pendingRelease != null)
            {
                Remove(pendingRelease, blocklist);

                return;
            }

            var trackedDownload = GetTrackedDownload(id);

            if (trackedDownload == null)
            {
                throw new NotFoundException();
            }

            Remove(trackedDownload, removeFromClient, blocklist, skipRedownload, changeCategory);
            _trackedDownloadService.StopTracking(trackedDownload.DownloadItem.DownloadId);
        }

        [HttpDelete("bulk")]
        public object RemoveMany([FromBody] QueueBulkResource resource, [FromQuery] bool removeFromClient = true, [FromQuery] bool blocklist = false, [FromQuery] bool skipRedownload = false, [FromQuery] bool changeCategory = false)
        {
            var trackedDownloadIds = new List<string>();
            var pendingToRemove = new List<Workarr.Queue.Queue>();
            var trackedToRemove = new List<TrackedDownload>();

            foreach (var id in resource.Ids)
            {
                var pendingRelease = _pendingReleaseService.FindPendingQueueItem(id);

                if (pendingRelease != null)
                {
                    pendingToRemove.Add(pendingRelease);
                    continue;
                }

                var trackedDownload = GetTrackedDownload(id);

                if (trackedDownload != null)
                {
                    trackedToRemove.Add(trackedDownload);
                }
            }

            foreach (var pendingRelease in pendingToRemove.DistinctBy(p => p.Id))
            {
                Remove(pendingRelease, blocklist);
            }

            foreach (var trackedDownload in trackedToRemove.DistinctBy(t => t.DownloadItem.DownloadId))
            {
                Remove(trackedDownload, removeFromClient, blocklist, skipRedownload, changeCategory);
                trackedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
            }

            _trackedDownloadService.StopTracking(trackedDownloadIds);

            return new { };
        }

        [HttpGet]
        [Produces("application/json")]
        public PagingResource<QueueResource> GetQueue([FromQuery] PagingRequestResource paging, bool includeUnknownSeriesItems = false, bool includeSeries = false, bool includeEpisode = false, [FromQuery] int[] seriesIds = null, DownloadProtocol? protocol = null, [FromQuery] int[] languages = null, [FromQuery] int[] quality = null, [FromQuery] QueueStatus[] status = null)
        {
            var pagingResource = new PagingResource<QueueResource>(paging);
            var pagingSpec = pagingResource.MapToPagingSpec<QueueResource, Workarr.Queue.Queue>(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "added",
                    "downloadClient",
                    "episode",
                    "episode.airDateUtc",
                    "episode.title",
                    "episodes.airDateUtc",
                    "episodes.title",
                    "estimatedCompletionTime",
                    "indexer",
                    "language",
                    "languages",
                    "progress",
                    "protocol",
                    "quality",
                    "series.sortTitle",
                    "size",
                    "status",
                    "timeleft",
                    "title"
                },
                "timeleft",
                SortDirection.Ascending);

            return pagingSpec.ApplyToPage((spec) => GetQueue(spec, seriesIds?.ToHashSet(), protocol, languages?.ToHashSet(), quality?.ToHashSet(), status?.ToHashSet(), includeUnknownSeriesItems), (q) => MapToResource(q, includeSeries, includeEpisode));
        }

        private PagingSpec<Workarr.Queue.Queue> GetQueue(PagingSpec<Workarr.Queue.Queue> pagingSpec, HashSet<int> seriesIds, DownloadProtocol? protocol, HashSet<int> languages, HashSet<int> quality, HashSet<QueueStatus> status, bool includeUnknownSeriesItems)
        {
            var ascending = pagingSpec.SortDirection == SortDirection.Ascending;
            var orderByFunc = GetOrderByFunc(pagingSpec);

            var queue = _queueService.GetQueue();
            var filteredQueue = includeUnknownSeriesItems ? queue : queue.Where(q => q.Series != null);
            var pending = _pendingReleaseService.GetPendingQueue();

            var hasSeriesIdFilter = seriesIds.Any();
            var hasLanguageFilter = languages.Any();
            var hasQualityFilter = quality.Any();
            var hasStatusFilter = status.Any();

            var fullQueue = filteredQueue.Concat(pending).Where(q =>
            {
                var include = true;

                if (hasSeriesIdFilter)
                {
                    include &= q.Series != null && seriesIds.Contains(q.Series.Id);
                }

                if (include && protocol.HasValue)
                {
                    include &= q.Protocol == protocol.Value;
                }

                if (include && hasLanguageFilter)
                {
                    include &= q.Languages.Any(l => languages.Contains(l.Id));
                }

                if (include && hasQualityFilter)
                {
                    include &= quality.Contains(q.Quality.Quality.Id);
                }

                if (include && hasStatusFilter)
                {
                    include &= status.Contains(q.Status);
                }

                return include;
            }).ToList();

            IOrderedEnumerable<Workarr.Queue.Queue> ordered;

            if (pagingSpec.SortKey == "timeleft")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.TimeLeft, new TimeleftComparer())
                    : fullQueue.OrderByDescending(q => q.TimeLeft, new TimeleftComparer());
            }
            else if (pagingSpec.SortKey == "estimatedCompletionTime")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.EstimatedCompletionTime, new DatetimeComparer())
                    : fullQueue.OrderByDescending(q => q.EstimatedCompletionTime,
                        new DatetimeComparer());
            }
            else if (pagingSpec.SortKey == "added")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Added, new DatetimeComparer())
                    : fullQueue.OrderByDescending(q => q.Added,
                        new DatetimeComparer());
            }
            else if (pagingSpec.SortKey == "protocol")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Protocol)
                    : fullQueue.OrderByDescending(q => q.Protocol);
            }
            else if (pagingSpec.SortKey == "indexer")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Indexer, StringComparer.InvariantCultureIgnoreCase)
                    : fullQueue.OrderByDescending(q => q.Indexer, StringComparer.InvariantCultureIgnoreCase);
            }
            else if (pagingSpec.SortKey == "downloadClient")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.DownloadClient, StringComparer.InvariantCultureIgnoreCase)
                    : fullQueue.OrderByDescending(q => q.DownloadClient, StringComparer.InvariantCultureIgnoreCase);
            }
            else if (pagingSpec.SortKey == "quality")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Quality, _qualityComparer)
                    : fullQueue.OrderByDescending(q => q.Quality, _qualityComparer);
            }
            else if (pagingSpec.SortKey == "languages")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Languages, new LanguagesComparer())
                    : fullQueue.OrderByDescending(q => q.Languages, new LanguagesComparer());
            }
            else
            {
                ordered = ascending ? fullQueue.OrderBy(orderByFunc) : fullQueue.OrderByDescending(orderByFunc);
            }

            ordered = ordered.ThenByDescending(q => q.Size == 0 ? 0 : 100 - (q.SizeLeft / q.Size * 100));

            pagingSpec.Records = ordered.Skip((pagingSpec.Page - 1) * pagingSpec.PageSize).Take(pagingSpec.PageSize).ToList();
            pagingSpec.TotalRecords = fullQueue.Count;

            if (pagingSpec.Records.Empty() && pagingSpec.Page > 1)
            {
                pagingSpec.Page = (int)Math.Max(Math.Ceiling((decimal)(pagingSpec.TotalRecords / pagingSpec.PageSize)), 1);
                pagingSpec.Records = ordered.Skip((pagingSpec.Page - 1) * pagingSpec.PageSize).Take(pagingSpec.PageSize).ToList();
            }

            return pagingSpec;
        }

        private Func<Workarr.Queue.Queue, object> GetOrderByFunc(PagingSpec<Workarr.Queue.Queue> pagingSpec)
        {
            switch (pagingSpec.SortKey)
            {
                case "status":
                    return q => q.Status.ToString();
                case "series.sortTitle":
                    return q => q.Series?.SortTitle ?? q.Title;
                case "title":
                    return q => q.Title;
                case "episode":
                    return q => q.Episode;
                case "episode.airDateUtc":
                case "episodes.airDateUtc":
                    return q => q.Episode?.AirDateUtc ?? DateTime.MinValue;
                case "episode.title":
                case "episodes.title":
                    return q => q.Episode?.Title ?? string.Empty;
                case "language":
                case "languages":
                    return q => q.Languages;
                case "quality":
                    return q => q.Quality;
                case "size":
                    return q => q.Size;
                case "progress":
                    // Avoid exploding if a download's size is 0
                    return q => 100 - (q.SizeLeft / Math.Max(q.Size * 100, 1));
                default:
                    return q => q.TimeLeft;
            }
        }

        private void Remove(Workarr.Queue.Queue pendingRelease, bool blocklist)
        {
            if (blocklist)
            {
                _blocklistService.Block(pendingRelease.RemoteEpisode, "Pending release manually blocklisted");
            }

            _pendingReleaseService.RemovePendingQueueItems(pendingRelease.Id);
        }

        private TrackedDownload Remove(TrackedDownload trackedDownload, bool removeFromClient, bool blocklist, bool skipRedownload, bool changeCategory)
        {
            if (removeFromClient)
            {
                var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);

                if (downloadClient == null)
                {
                    throw new BadRequestException();
                }

                downloadClient.RemoveItem(trackedDownload.DownloadItem, true);
            }
            else if (changeCategory)
            {
                var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);

                if (downloadClient == null)
                {
                    throw new BadRequestException();
                }

                downloadClient.MarkItemAsImported(trackedDownload.DownloadItem);
            }

            if (blocklist)
            {
                _failedDownloadService.MarkAsFailed(trackedDownload, skipRedownload);
            }

            if (!removeFromClient && !blocklist && !changeCategory)
            {
                if (!_ignoredDownloadService.IgnoreDownload(trackedDownload))
                {
                    return null;
                }
            }

            return trackedDownload;
        }

        private TrackedDownload GetTrackedDownload(int queueId)
        {
            var queueItem = _queueService.Find(queueId);

            if (queueItem == null)
            {
                throw new NotFoundException();
            }

            var trackedDownload = _trackedDownloadService.Find(queueItem.DownloadId);

            if (trackedDownload == null)
            {
                throw new NotFoundException();
            }

            return trackedDownload;
        }

        private QueueResource MapToResource(Workarr.Queue.Queue queueItem, bool includeSeries, bool includeEpisode)
        {
            return queueItem.ToResource(includeSeries, includeEpisode);
        }

        [NonAction]
        public void Handle(QueueUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }

        [NonAction]
        public void Handle(PendingReleasesUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }
    }
}
