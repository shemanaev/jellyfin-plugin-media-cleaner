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
    LibraryMenu.setTabs('MediaCleaner', commons.TabUsers, commons.getTabs)
    Dashboard.showLoadingMsg()

    const $UsersPlayedMode = page.querySelector('#UsersPlayedMode')
    const $UsersFavoritedMode = page.querySelector('#UsersFavoritedMode')
    const $IgnorePlayedList = page.querySelector('#IgnorePlayedList')
    const $IgnoreFavoritedList = page.querySelector('#IgnoreFavoritedList')

    $UsersPlayedMode.addEventListener('change', usersPlayedModeChanged)
    $UsersFavoritedMode.addEventListener('change', usersFavoritedModeChanged)

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

            $UsersPlayedMode.value = config.UsersPlayedMode
            $UsersFavoritedMode.value = config.UsersFavoritedMode

            commons.fireEvent([
                $UsersPlayedMode,
                $UsersFavoritedMode,
            ], 'change')

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

        config.UsersPlayedMode = form.querySelector('#UsersPlayedMode').value
        config.UsersFavoritedMode = form.querySelector('#UsersFavoritedMode').value

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

function usersPlayedModeChanged(event) {
    const field = this.parentNode.querySelector('.fieldDescription')
    switch (this.value) {
        case 'Ignore':
            field.innerHTML = 'Ignore played state of following users'
            break

        case 'Acknowledge':
            field.innerHTML = 'Use played state only of following users'
            break

        default:
            field.innerHTML = ''
    }
}

function usersFavoritedModeChanged(event) {
    const field = this.parentNode.querySelector('.fieldDescription')
    switch (this.value) {
        case 'Ignore':
            field.innerHTML = 'Ignore favorites of following users'
            break

        case 'Acknowledge':
            field.innerHTML = 'Use favorites only of following users'
            break

        default:
            field.innerHTML = ''
    }
}
