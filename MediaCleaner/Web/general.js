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
    commons.setTabs('MediaCleaner', commons.TabGeneral, commons.getTabs)
    Dashboard.showLoadingMsg()

    const $KeepPlayedMovies = page.querySelector('#KeepPlayedMovies')
    const $KeepPlayedEpisodes = page.querySelector('#KeepPlayedEpisodes')
    const $KeepPlayedVideos = page.querySelector('#KeepPlayedVideos')
    const $KeepPlayedAudio = page.querySelector('#KeepPlayedAudio')
    const $KeepPlayedAudioBooks = page.querySelector('#KeepPlayedAudioBooks')
    $KeepPlayedMovies.addEventListener('change', keepPlayedChanged)
    $KeepPlayedEpisodes.addEventListener('change', keepPlayedChanged)
    $KeepPlayedVideos.addEventListener('change', keepPlayedChanged)
    $KeepPlayedAudio.addEventListener('change', keepPlayedChanged)
    $KeepPlayedAudioBooks.addEventListener('change', keepPlayedChanged)

    const $KeepFavoriteMovies = page.querySelector('#KeepFavoriteMovies')
    const $KeepFavoriteEpisodes = page.querySelector('#KeepFavoriteEpisodes')
    const $KeepFavoriteVideos = page.querySelector('#KeepFavoriteVideos')
    const $KeepFavoriteAudio = page.querySelector('#KeepFavoriteAudio')
    const $KeepFavoriteAudioBooks = page.querySelector('#KeepFavoriteAudioBooks')
    $KeepFavoriteMovies.addEventListener('change', keepFavoriteChanged)
    $KeepFavoriteEpisodes.addEventListener('change', keepFavoriteChanged)
    $KeepFavoriteVideos.addEventListener('change', keepFavoriteChanged)
    $KeepFavoriteAudio.addEventListener('change', keepFavoriteChanged)
    $KeepFavoriteAudioBooks.addEventListener('change', keepFavoriteChanged)

    const $DeleteEpisodes = page.querySelector('#DeleteEpisodes')
    $DeleteEpisodes.addEventListener('change', deleteEpisodesChanged)

    const $MarkAsUnplayed = page.querySelector('#MarkAsUnplayed')
    $MarkAsUnplayed.addEventListener('change', markAsUnplayedChanged)

    const $CountAsNotPlayedAfter = page.querySelector('#CountAsNotPlayedAfter')
    $CountAsNotPlayedAfter.addEventListener('change', countAsNotPlayedAfterChanged)

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        page.querySelector('#KeepMoviesFor').value = config.KeepMoviesFor
        page.querySelector('#KeepMoviesNotPlayedFor').value = config.KeepMoviesNotPlayedFor
        $KeepPlayedMovies.value = config.KeepPlayedMovies
        $KeepFavoriteMovies.value = config.KeepFavoriteMovies

        page.querySelector('#KeepEpisodesFor').value = config.KeepEpisodesFor
        page.querySelector('#KeepEpisodesNotPlayedFor').value = config.KeepEpisodesNotPlayedFor
        page.querySelector('#DeleteEpisodes').value = config.DeleteEpisodes
        $KeepPlayedEpisodes.value = config.KeepPlayedEpisodes
        $KeepFavoriteEpisodes.value = config.KeepFavoriteEpisodes

        page.querySelector('#KeepVideosFor').value = config.KeepVideosFor
        page.querySelector('#KeepVideosNotPlayedFor').value = config.KeepVideosNotPlayedFor
        $KeepPlayedVideos.value = config.KeepPlayedVideos
        $KeepFavoriteVideos.value = config.KeepFavoriteVideos

        page.querySelector('#KeepAudioFor').value = config.KeepAudioFor
        page.querySelector('#KeepAudioNotPlayedFor').value = config.KeepAudioNotPlayedFor
        $KeepPlayedAudio.value = config.KeepPlayedAudio
        $KeepFavoriteAudio.value = config.KeepFavoriteAudio

        page.querySelector('#KeepAudioBooksFor').value = config.KeepAudioBooksFor
        page.querySelector('#KeepAudioBooksNotPlayedFor').value = config.KeepAudioBooksNotPlayedFor
        $KeepPlayedAudioBooks.value = config.KeepPlayedAudioBooks
        $KeepFavoriteAudioBooks.value = config.KeepFavoriteAudioBooks

        page.querySelector('#MarkAsUnplayed').checked = config.MarkAsUnplayed
        page.querySelector('#CountAsNotPlayedAfter').value = config.CountAsNotPlayedAfter

        commons.fireEvent([
            $KeepPlayedMovies,
            $KeepPlayedEpisodes,
            $KeepPlayedVideos,
            $KeepPlayedAudio,
            $KeepPlayedAudioBooks,
            $KeepFavoriteMovies,
            $KeepFavoriteEpisodes,
            $KeepFavoriteVideos,
            $KeepFavoriteAudio,
            $KeepFavoriteAudioBooks,
            $DeleteEpisodes,
            $MarkAsUnplayed,
            $CountAsNotPlayedAfter,
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
        config.KeepMoviesNotPlayedFor = form.querySelector('#KeepMoviesNotPlayedFor').value
        config.KeepPlayedMovies = form.querySelector('#KeepPlayedMovies').value
        config.KeepFavoriteMovies = form.querySelector('#KeepFavoriteMovies').value

        config.KeepEpisodesFor = form.querySelector('#KeepEpisodesFor').value
        config.KeepEpisodesNotPlayedFor = form.querySelector('#KeepEpisodesNotPlayedFor').value
        config.DeleteEpisodes = form.querySelector('#DeleteEpisodes').value
        config.KeepPlayedEpisodes = form.querySelector('#KeepPlayedEpisodes').value
        config.KeepFavoriteEpisodes = form.querySelector('#KeepFavoriteEpisodes').value

        config.KeepVideosFor = form.querySelector('#KeepVideosFor').value
        config.KeepVideosNotPlayedFor = form.querySelector('#KeepVideosNotPlayedFor').value
        config.KeepPlayedVideos = form.querySelector('#KeepPlayedVideos').value
        config.KeepFavoriteVideos = form.querySelector('#KeepFavoriteVideos').value

        config.KeepAudioFor = form.querySelector('#KeepAudioFor').value
        config.KeepAudioNotPlayedFor = form.querySelector('#KeepAudioNotPlayedFor').value
        config.KeepPlayedAudio = form.querySelector('#KeepPlayedAudio').value
        config.KeepFavoriteAudio = form.querySelector('#KeepFavoriteAudio').value

        config.KeepAudioBooksFor = form.querySelector('#KeepAudioBooksFor').value
        config.KeepAudioBooksNotPlayedFor = form.querySelector('#KeepAudioBooksNotPlayedFor').value
        config.KeepPlayedAudioBooks = form.querySelector('#KeepPlayedAudioBooks').value
        config.KeepFavoriteAudioBooks = form.querySelector('#KeepFavoriteAudioBooks').value

        config.MarkAsUnplayed = form.querySelector('#MarkAsUnplayed').checked
        config.CountAsNotPlayedAfter = form.querySelector('#CountAsNotPlayedAfter').value

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
            field.innerHTML = 'At least one user have fully played item (countdown from first playback)'
            break

        case 'AnyUserRolling':
            field.innerHTML = 'At least one user have fully played item (countdown from latest playback, extends period on any in-progress playback)'
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

function countAsNotPlayedAfterChanged(event) {
    const field = this.parentNode.querySelector('.fieldDescription')
    if (this.value == -1) {
        const exampleValue = 10
        const now = new Date()
        const edgeDate = addDays(-exampleValue)
        const playDate = addDays(-exampleValue - 5)
        field.innerHTML = `For example: the value is set to ${exampleValue}, the last time the item was played on <b>${playDate.toLocaleDateString()}</b>, it was before <b>${edgeDate.toLocaleDateString()}</b> (${now.toLocaleDateString()} - ${exampleValue} days) so this view will be considered as not played. -1 to disable`
    } else {
        const playDate = addDays(-this.value)
        field.innerHTML = `All plays before <b>${playDate.toLocaleDateString()}</b> will not be counted. Set to <i>-1</i> to disable this behavior`
    }
}

function addDays(days) {
    const now = new Date()
    const future = new Date(new Date(now).setDate(now.getDate() + Number(days)))
    return future
}
