using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.SeriesStats;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.Profiles.Languages;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;
using Sonarr.Http;
using Sonarr.Http.Extensions;

namespace NzbDrone.Api.Series
{
    public class SeriesModule : SonarrRestModuleWithSignalR<SeriesResource, Core.Tv.Series>, 
                                IHandle<EpisodeImportedEvent>, 
                                IHandle<EpisodeFileDeletedEvent>,
                                IHandle<SeriesUpdatedEvent>,       
                                IHandle<SeriesEditedEvent>,  
                                IHandle<SeriesDeletedEvent>,
                                IHandle<SeriesRenamedEvent>,
                                IHandle<MediaCoversUpdatedEvent>

    {
        private readonly ISeriesService _seriesService;
        private readonly IAddSeriesService _addSeriesService;
        private readonly ISeriesStatisticsService _seriesStatisticsService;
        private readonly ISceneMappingService _sceneMappingService;
        private readonly IMapCoversToLocal _coverMapper;
        private readonly ILanguageProfileService _languageProfileService;

        public SeriesModule(IBroadcastSignalRMessage signalRBroadcaster,
                            ISeriesService seriesService,
                            IAddSeriesService addSeriesService,
                            ISeriesStatisticsService seriesStatisticsService,
                            ISceneMappingService sceneMappingService,
                            IMapCoversToLocal coverMapper,
                            ILanguageProfileService languageProfileService,
                            RootFolderValidator rootFolderValidator,
                            SeriesPathValidator seriesPathValidator,
                            SeriesExistsValidator seriesExistsValidator,
                            SeriesAncestorValidator seriesAncestorValidator,
                            SystemFolderValidator systemFolderValidator,
                            ProfileExistsValidator profileExistsValidator,
                            LanguageProfileExistsValidator languageProfileExistsValidator
            )
            : base(signalRBroadcaster)
        {
            _seriesService = seriesService;
            _addSeriesService = addSeriesService;
            _seriesStatisticsService = seriesStatisticsService;
            _sceneMappingService = sceneMappingService;

            _coverMapper = coverMapper;
            _languageProfileService = languageProfileService;

            GetResourceAll = AllSeries;
            GetResourceById = GetSeries;
            CreateResource = AddSeries;
            UpdateResource = UpdateSeries;
            DeleteResource = DeleteSeries;

            SharedValidator.RuleFor(s => s.ProfileId).ValidId();
            SharedValidator.RuleFor(s => s.LanguageProfileId);

            SharedValidator.RuleFor(s => s.Path)
                           .Cascade(CascadeMode.StopOnFirstFailure)
                           .IsValidPath()
                           .SetValidator(rootFolderValidator)
                           .SetValidator(seriesPathValidator)
                           .SetValidator(seriesAncestorValidator)
                           .SetValidator(systemFolderValidator)
                           .When(s => !s.Path.IsNullOrWhiteSpace());

            SharedValidator.RuleFor(s => s.ProfileId).SetValidator(profileExistsValidator);

            PostValidator.RuleFor(s => s.Path).IsValidPath().When(s => s.RootFolderPath.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.RootFolderPath).IsValidPath().When(s => s.Path.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.TvdbId).GreaterThan(0).SetValidator(seriesExistsValidator);
            PostValidator.RuleFor(s => s.LanguageProfileId).SetValidator(languageProfileExistsValidator).When(s => s.LanguageProfileId != 0);

            PutValidator.RuleFor(s => s.Path).IsValidPath();

            // Ensure any editing has a valid LanguageProfile
            PutValidator.RuleFor(s => s.LanguageProfileId).SetValidator(languageProfileExistsValidator);
        }

        private SeriesResource GetSeries(int id)
        {
            var includeSeasonImages = Context != null && Request.GetBooleanQueryParameter("includeSeasonImages");

            var series = _seriesService.GetSeries(id);
            return MapToResource(series, includeSeasonImages);
        }

        private List<SeriesResource> AllSeries()
        {
            var includeSeasonImages = Request.GetBooleanQueryParameter("includeSeasonImages");
            var seriesStats = _seriesStatisticsService.SeriesStatistics();
            var seriesResources = _seriesService.GetAllSeries().Select(s => s.ToResource(includeSeasonImages)).ToList();

            MapCoversToLocal(seriesResources.ToArray());
            LinkSeriesStatistics(seriesResources, seriesStats);
            PopulateAlternateTitles(seriesResources);

            return seriesResources;
        }

        private int AddSeries(SeriesResource seriesResource)
        {
            var model = seriesResource.ToModel();

            // Set a default LanguageProfileId to maintain backwards compatibility with apps using the v2 API
            if (model.LanguageProfileId == 0 || !_languageProfileService.Exists(model.LanguageProfileId))
            {
                model.LanguageProfileId = _languageProfileService.All().First().Id;
            }

            return _addSeriesService.AddSeries(model).Id;
        }

        private void UpdateSeries(SeriesResource seriesResource)
        {
            var model = seriesResource.ToModel(_seriesService.GetSeries(seriesResource.Id));

            _seriesService.UpdateSeries(model);

            BroadcastResourceChange(ModelAction.Updated, seriesResource);
        }

        private void DeleteSeries(int id)
        {
            var deleteFiles = false;
            var deleteFilesQuery = Request.Query.deleteFiles;

            if (deleteFilesQuery.HasValue)
            {
                deleteFiles = Convert.ToBoolean(deleteFilesQuery.Value);
            }

            _seriesService.DeleteSeries(id, deleteFiles, false);
        }

        private SeriesResource MapToResource(Core.Tv.Series series, bool includeSeasonImages)
        {
            if (series == null) return null;

            var resource = series.ToResource(includeSeasonImages);
            MapCoversToLocal(resource);
            FetchAndLinkSeriesStatistics(resource);
            PopulateAlternateTitles(resource);

            return resource;
        }

        private void MapCoversToLocal(params SeriesResource[] series)
        {
            foreach (var seriesResource in series)
            {
                _coverMapper.ConvertToLocalUrls(seriesResource.Id, seriesResource.Images);
            }
        }

        private void FetchAndLinkSeriesStatistics(SeriesResource resource)
        {
            LinkSeriesStatistics(resource, _seriesStatisticsService.SeriesStatistics(resource.Id));
        }

        private void LinkSeriesStatistics(List<SeriesResource> resources, List<SeriesStatistics> seriesStatistics)
        {
            var dictSeriesStats = seriesStatistics.ToDictionary(v => v.SeriesId);

            foreach (var series in resources)
            {
                var stats = dictSeriesStats.GetValueOrDefault(series.Id);
                if (stats == null) continue;

                LinkSeriesStatistics(series, stats);
            }
        }

        private void LinkSeriesStatistics(SeriesResource resource, SeriesStatistics seriesStatistics)
        {
            resource.TotalEpisodeCount = seriesStatistics.TotalEpisodeCount;
            resource.EpisodeCount = seriesStatistics.EpisodeCount;
            resource.EpisodeFileCount = seriesStatistics.EpisodeFileCount;
            resource.NextAiring = seriesStatistics.NextAiring;
            resource.PreviousAiring = seriesStatistics.PreviousAiring;
            resource.SizeOnDisk = seriesStatistics.SizeOnDisk;

            if (seriesStatistics.SeasonStatistics != null)
            {
                var dictSeasonStats = seriesStatistics.SeasonStatistics.ToDictionary(v => v.SeasonNumber);

                foreach (var season in resource.Seasons)
                {
                    season.Statistics = dictSeasonStats.GetValueOrDefault(season.SeasonNumber).ToResource();
                }
            }
        }

        private void PopulateAlternateTitles(List<SeriesResource> resources)
        {
            foreach (var resource in resources)
            {
                PopulateAlternateTitles(resource);
            }
        }

        private void PopulateAlternateTitles(SeriesResource resource)
        {
            var mappings = _sceneMappingService.FindByTvdbId(resource.TvdbId);

            if (mappings == null) return;

            resource.AlternateTitles = mappings.Select(v => new AlternateTitleResource
                                                            {
                                                                Title = v.Title,
                                                                SeasonNumber = v.SeasonNumber,
                                                                SceneSeasonNumber = v.SceneSeasonNumber,
                                                                SceneOrigin = v.SceneOrigin,
                                                                Comment = v.Comment
                                                            }).ToList();
        }

        public void Handle(EpisodeImportedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, message.ImportedEpisode.SeriesId);
        }

        public void Handle(EpisodeFileDeletedEvent message)
        {
            if (message.Reason == DeleteMediaFileReason.Upgrade) return;

            BroadcastResourceChange(ModelAction.Updated, message.EpisodeFile.SeriesId);
        }

        public void Handle(SeriesUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, message.Series.Id);
        }

        public void Handle(SeriesEditedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, message.Series.Id);
        }

        public void Handle(SeriesDeletedEvent message)
        {
            BroadcastResourceChange(ModelAction.Deleted, message.Series.ToResource());
        }

        public void Handle(SeriesRenamedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, message.Series.Id);
        }

        public void Handle(MediaCoversUpdatedEvent message)
        {
            if (message.Updated)
            {
                BroadcastResourceChange(ModelAction.Updated, message.Series.Id);
            }
        }
    }
}
