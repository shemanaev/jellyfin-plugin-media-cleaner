export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })

    view.querySelector('#MediaCleanerConfigForm').addEventListener('submit', function (e) {
        import(commonsUrl).then(onFormSubmit.bind(this))
        e.preventDefault()
        return false
    })
}

function onViewShow(commons) {
    const page = this
    LibraryMenu.setTabs('MediaCleaner', commons.TabsGeneral, commons.getTabs)
    Dashboard.showLoadingMsg()

    const $KeepPlayedMovies = page.querySelector('#KeepPlayedMovies')
    const $KeepPlayedEpisodes = page.querySelector('#KeepPlayedEpisodes')
    const $KeepPlayedVideos = page.querySelector('#KeepPlayedVideos')
    $KeepPlayedMovies.addEventListener('change', keepPlayedChanged)
    $KeepPlayedEpisodes.addEventListener('change', keepPlayedChanged)
    $KeepPlayedVideos.addEventListener('change', keepPlayedChanged)

    const $KeepFavoriteMovies = page.querySelector('#KeepFavoriteMovies')
    const $KeepFavoriteEpisodes = page.querySelector('#KeepFavoriteEpisodes')
    const $KeepFavoriteVideos = page.querySelector('#KeepFavoriteVideos')
    $KeepFavoriteMovies.addEventListener('change', keepFavoriteChanged)
    $KeepFavoriteEpisodes.addEventListener('change', keepFavoriteChanged)
    $KeepFavoriteVideos.addEventListener('change', keepFavoriteChanged)

    const $DeleteEpisodes = page.querySelector('#DeleteEpisodes')
    $DeleteEpisodes.addEventListener('change', deleteEpisodesChanged)

    const $MarkAsUnplayed = page.querySelector('#MarkAsUnplayed')
    $MarkAsUnplayed.addEventListener('change', markAsUnplayedChanged)

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        page.querySelector('#KeepMoviesFor').value = config.KeepMoviesFor
        $KeepPlayedMovies.value = config.KeepPlayedMovies
        $KeepFavoriteMovies.value = config.KeepFavoriteMovies
        page.querySelector('#KeepEpisodesFor').value = config.KeepEpisodesFor
        page.querySelector('#DeleteEpisodes').value = config.DeleteEpisodes
        $KeepPlayedEpisodes.value = config.KeepPlayedEpisodes
        $KeepFavoriteEpisodes.value = config.KeepFavoriteEpisodes
        page.querySelector('#KeepVideosFor').value = config.KeepVideosFor
        $KeepPlayedVideos.value = config.KeepPlayedVideos
        $KeepFavoriteVideos.value = config.KeepFavoriteVideos
        page.querySelector('#MarkAsUnplayed').checked = config.MarkAsUnplayed

        commons.fireEvent([
            $KeepPlayedMovies,
            $KeepPlayedEpisodes,
            $KeepPlayedVideos,
            $KeepFavoriteMovies,
            $KeepFavoriteEpisodes,
            $KeepFavoriteVideos,
            $DeleteEpisodes,
            $MarkAsUnplayed,
        ], 'change')

        Dashboard.hideLoadingMsg()
    })

    const request = {
        url: ApiClient.getUrl('MediaCleaner/Test'),
    }


    ApiClient.fetch(request).then(function (result) {
        console.log(result)
    }).catch(function (error) {
        console.log(error)
    })
}

function onFormSubmit(commons) {
    const form = this
    Dashboard.showLoadingMsg()

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.KeepMoviesFor = form.querySelector('#KeepMoviesFor').value
        config.KeepPlayedMovies = form.querySelector('#KeepPlayedMovies').value
        config.KeepFavoriteMovies = form.querySelector('#KeepFavoriteMovies').value
        config.KeepEpisodesFor = form.querySelector('#KeepEpisodesFor').value
        config.DeleteEpisodes = form.querySelector('#DeleteEpisodes').value
        config.KeepPlayedEpisodes = form.querySelector('#KeepPlayedEpisodes').value
        config.KeepFavoriteEpisodes = form.querySelector('#KeepFavoriteEpisodes').value
        config.KeepVideosFor = form.querySelector('#KeepVideosFor').value
        config.KeepPlayedVideos = form.querySelector('#KeepPlayedVideos').value
        config.KeepFavoriteVideos = form.querySelector('#KeepFavoriteVideos').value
        config.MarkAsUnplayed = form.querySelector('#MarkAsUnplayed').checked

        ApiClient.updatePluginConfiguration(commons.pluginId, config).then(result => {
            Dashboard.processPluginConfigurationUpdateResult(result)
        })
    })
}

function keepFavoriteChanged(event) {
    const field = this.parentNode.querySelector('.fieldDescription')
    switch (this.value) {
        case 'AnyUser':
            field.innerHTML = 'At least one user have item in favorites'
            break

        case 'AllUsers':
            field.innerHTML = 'All users have item in favorites'
            break

        default:
            field.innerHTML = ''
    }
}

function keepPlayedChanged(event) {
    const field = this.parentNode.querySelector('.fieldDescription')
    switch (this.value) {
        case 'AnyUser':
            field.innerHTML = 'At least one user have fully played item'
            break

        case 'AllUsers':
            field.innerHTML = 'All users have fully played item'
            break

        default:
            field.innerHTML = ''
    }
}

function deleteEpisodesChanged(event) {
    const field = this.parentNode.querySelector('.fieldDescription')
    switch (this.value) {
        case 'SeriesEnded':
            field.innerHTML = `Don't delete unless series status changes to "Ended" in metadata`
            break

        default:
            field.innerHTML = ''
    }
}

function markAsUnplayedChanged(event) {
    const field = this.parentNode.parentNode.querySelector('.fieldDescription')
    if (this.checked) {
        field.innerHTML = `Items will be marked as unplayed when deleted and will not be removed when re-added`
    } else {
        field.innerHTML = `Re-added items that have already been removed by cleaner will be deleted again the first time the scheduled task is run`
    }
}
