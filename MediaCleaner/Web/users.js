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
    LibraryMenu.setTabs('MediaCleaner', 1, commons.getTabs)
    Dashboard.showLoadingMsg()

    const $IgnorePlayedList = page.querySelector('#IgnorePlayedList')
    const $IgnoreFavoritedList = page.querySelector('#IgnoreFavoritedList')

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        ApiClient.getUsers().then(users => {
            let playedHtml = '<div data-role="controlgroup">'
            let favoritedHtml = '<div data-role="controlgroup">'
            for (let user of users) {
                const ignorePlayed = config.UsersIgnorePlayed.find(e => e == user.Id)
                playedHtml += getUserHtml(user, ignorePlayed != null)
                const ignoreFavorited = config.UsersIgnoreFavorited.find(e => e == user.Id)
                favoritedHtml += getUserHtml(user, ignoreFavorited != null)
            }
            playedHtml += '</div>'
            favoritedHtml += '</div>'

            $IgnorePlayedList.innerHTML = playedHtml
            $IgnoreFavoritedList.innerHTML = favoritedHtml

            Dashboard.hideLoadingMsg()
        })
    })
}

function onFormSubmit(commons) {
    const form = this
    Dashboard.showLoadingMsg()

    const $IgnorePlayedList = form.querySelector('#IgnorePlayedList')
    const $IgnoreFavoritedList = form.querySelector('#IgnoreFavoritedList')

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {

        config.UsersIgnorePlayed = Array.prototype.map.call($IgnorePlayedList.querySelectorAll('input:checked'),
            elem => elem.getAttribute('data-userid'))
        config.UsersIgnoreFavorited = Array.prototype.map.call($IgnoreFavoritedList.querySelectorAll('input:checked'),
            elem => elem.getAttribute('data-userid'))

        ApiClient.updatePluginConfiguration(commons.pluginId, config).then(result => {
            Dashboard.processPluginConfigurationUpdateResult(result)
        })
    })
}

function getUserHtml(user, isChecked) {
    const checkedAttribute = isChecked ? ' checked="checked" ' : ''
    let html = '<label>'
    html += '<input is="emby-checkbox" type="checkbox" data-mini="true" data-userid="' + user.Id + '"' + checkedAttribute + ' />'
    html += '<span>' + user.Name + '</span></label>'
    return html
}
