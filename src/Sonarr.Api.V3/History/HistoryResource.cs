using System;
using System.Collections.Generic;
using Sonarr.Api.V3.CustomFormats;
using Sonarr.Api.V3.Episodes;
using Sonarr.Api.V3.Series;
using Sonarr.Http.REST;
using Workarr.CustomFormats;
using Workarr.History;
using Workarr.Qualities;

namespace Sonarr.Api.V3.History
{
    public class HistoryResource : RestResource
    {
        public int EpisodeId { get; set; }
        public int SeriesId { get; set; }
        public string SourceTitle { get; set; }
        public List<Workarr.Languages.Language> Languages { get; set; }
        public QualityModel Quality { get; set; }
        public List<CustomFormatResource> CustomFormats { get; set; }
        public int CustomFormatScore { get; set; }
        public bool QualityCutoffNotMet { get; set; }
        public DateTime Date { get; set; }
        public string DownloadId { get; set; }

        public EpisodeHistoryEventType EventType { get; set; }

        public Dictionary<string, string> Data { get; set; }

        public EpisodeResource Episode { get; set; }
        public SeriesResource Series { get; set; }
    }

    public static class HistoryResourceMapper
    {
        public static HistoryResource ToResource(this EpisodeHistory model, ICustomFormatCalculationService formatCalculator)
        {
            if (model == null)
            {
                return null;
            }

            var customFormats = formatCalculator.ParseCustomFormat(model, model.Series);
            var customFormatScore = model.Series.QualityProfile.Value.CalculateCustomFormatScore(customFormats);

            return new HistoryResource
            {
                Id = model.Id,

                EpisodeId = model.EpisodeId,
                SeriesId = model.SeriesId,
                SourceTitle = model.SourceTitle,
                Languages = model.Languages,
                Quality = model.Quality,
                CustomFormats = customFormats.ToResource(false),
                CustomFormatScore = customFormatScore,

                // QualityCutoffNotMet
                Date = model.Date,
                DownloadId = model.DownloadId,

                EventType = model.EventType,

                Data = model.Data

                // Episode
                // Series
            };
        }
    }
}
