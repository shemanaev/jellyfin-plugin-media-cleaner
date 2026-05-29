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

    view.querySelector('#TestRadarr').addEventListener('click', function (e) {
        testConnection('Radarr')
    })

    view.querySelector('#TestSonarr').addEventListener('click', function (e) {
        testConnection('Sonarr')
    })
}

function onViewShow(commons) {
    const page = this
    commons.setTabs('MediaCleaner', commons.TabIntegrations, commons.getTabs)
    Dashboard.showLoadingMsg()

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        page.querySelector('#RadarrEnabled').checked = config.RadarrEnabled === true
        page.querySelector('#RadarrBaseUrl').value = config.RadarrBaseUrl || ''
        page.querySelector('#RadarrApiKey').value = config.RadarrApiKey || ''
        page.querySelector('#RadarrTimeoutSeconds').value = config.RadarrTimeoutSeconds || 30

        page.querySelector('#SonarrEnabled').checked = config.SonarrEnabled === true
        page.querySelector('#SonarrBaseUrl').value = config.SonarrBaseUrl || ''
        page.querySelector('#SonarrApiKey').value = config.SonarrApiKey || ''
        page.querySelector('#SonarrTimeoutSeconds').value = config.SonarrTimeoutSeconds || 30

        Dashboard.hideLoadingMsg()
    })
}

function onFormSubmit(commons) {
    const form = this
    Dashboard.showLoadingMsg()

    ApiClient.getPluginConfiguration(commons.pluginId).then(config => {
        config.RadarrEnabled = form.querySelector('#RadarrEnabled').checked
        config.RadarrBaseUrl = form.querySelector('#RadarrBaseUrl').value
        config.RadarrApiKey = form.querySelector('#RadarrApiKey').value
        config.RadarrTimeoutSeconds = form.querySelector('#RadarrTimeoutSeconds').value

        config.SonarrEnabled = form.querySelector('#SonarrEnabled').checked
        config.SonarrBaseUrl = form.querySelector('#SonarrBaseUrl').value
        config.SonarrApiKey = form.querySelector('#SonarrApiKey').value
        config.SonarrTimeoutSeconds = form.querySelector('#SonarrTimeoutSeconds').value

        ApiClient.updatePluginConfiguration(commons.pluginId, config).then(result => {
            Dashboard.processPluginConfigurationUpdateResult(result)
        })
    })
}

function testConnection(name) {
    const result = document.querySelector(`#${name}TestResult`)
    result.textContent = 'Testing...'

    ApiClient.fetch({
        type: 'GET',
        url: ApiClient.getUrl(`MediaCleaner/Integrations/${name}/Test`),
        dataType: 'json'
    }).then(response => {
        result.textContent = formatConnectionResult(response)
    }).catch(error => {
        result.textContent = `Connection test failed: ${getErrorMessage(error)}`
    })
}

function formatConnectionResult(response) {
    const value = parseConnectionResponse(response)
    const message = value.Message || value.message || 'No response message returned.'
    const version = value.Version || value.version
    const versionText = version ? ` Version: ${version}` : ''

    return `${message}${versionText}`
}

function parseConnectionResponse(response) {
    if (typeof response === 'string') {
        try {
            return JSON.parse(response)
        } catch {
            return { message: response }
        }
    }

    return response || {}
}

function getErrorMessage(error) {
    if (!error) {
        return 'Unknown error'
    }

    return error.message || error.Message || String(error)
}
