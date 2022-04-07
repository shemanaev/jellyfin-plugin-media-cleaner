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
    LibraryMenu.setTabs('MediaCleaner', 2, commons.getTabs)
    Dashboard.showLoadingMsg()

    const $LocationsExcludedList = page.querySelector('#LocationsExcludedList')

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        ApiClient.getVirtualFolders().then(virtualFolders => {
            let locationsHtml = '<div data-role="controlgroup">'
            for (let folder of virtualFolders) {
                for (let location of folder.Locations) {
                    const isExcluded = config.LocationsExcluded.find(e => e.toLowerCase() == location.toLowerCase())
                    locationsHtml += getLocationHtml(location, isExcluded != null)
                }
            }
            locationsHtml += '</div>'

            $LocationsExcludedList.innerHTML = locationsHtml

            Dashboard.hideLoadingMsg()
        })
    })
}

function onFormSubmit(commons) {
    const form = this
    Dashboard.showLoadingMsg()

    const $LocationsExcludedList = form.querySelector('#LocationsExcludedList')

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.LocationsExcluded = Array.prototype.map.call($LocationsExcludedList.querySelectorAll('input:checked'),
            elem => elem.getAttribute('data-location'))

        ApiClient.updatePluginConfiguration(commons.pluginId, config).then(result => {
            Dashboard.processPluginConfigurationUpdateResult(result)
        })
    })
}

function getLocationHtml(location, isChecked) {
    const checkedAttribute = isChecked ? ' checked="checked" ' : ''
    let html = '<label>'
    html += '<input is="emby-checkbox" type="checkbox" data-mini="true" data-location="' + location + '"' + checkedAttribute + ' />'
    html += '<span>' + location + '</span></label>'
    return html
}
