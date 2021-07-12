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
    LibraryMenu.setTabs('MediaCleaner', 0, commons.getTabs)
    Dashboard.showLoadingMsg()

    const $KeepFavoriteMovies = page.querySelector('#KeepFavoriteMovies')
    const $KeepFavoriteEpisodes = page.querySelector('#KeepFavoriteEpisodes')
    $KeepFavoriteMovies.addEventListener('change', keepFavoriteChanged)
    $KeepFavoriteEpisodes.addEventListener('change', keepFavoriteChanged)

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        page.querySelector('#KeepMoviesFor').value = config.KeepMoviesFor
        page.querySelector('#KeepFavoriteMovies').value = config.KeepFavoriteMovies
        page.querySelector('#KeepEpisodesFor').value = config.KeepEpisodesFor
        page.querySelector('#DeleteEpisodes').value = config.DeleteEpisodes
        page.querySelector('#KeepFavoriteEpisodes').value = config.KeepFavoriteEpisodes

        fireEvent([$KeepFavoriteMovies, $KeepFavoriteEpisodes], 'change')

        Dashboard.hideLoadingMsg()
    })
}

function onFormSubmit(commons) {
    const form = this
    Dashboard.showLoadingMsg()

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.KeepMoviesFor = form.querySelector('#KeepMoviesFor').value
        config.KeepFavoriteMovies = form.querySelector('#KeepFavoriteMovies').value
        config.KeepEpisodesFor = form.querySelector('#KeepEpisodesFor').value
        config.DeleteEpisodes = form.querySelector('#DeleteEpisodes').value
        config.KeepFavoriteEpisodes = form.querySelector('#KeepFavoriteEpisodes').value
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

function fireEvent(elements, event) {
    elements = Array.isArray(elements) ? elements : [elements]
    elements.forEach(x => x.dispatchEvent(new Event(event)))
}
