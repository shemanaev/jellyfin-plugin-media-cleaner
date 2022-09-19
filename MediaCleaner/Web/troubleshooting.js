export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })
}

const pageSelector = '#MediaCleanerConfigPageTroubleshooting'
const logTextareaSelector = '#TroubleshootingLog'

function onViewShow(commons) {
    const page = this
    LibraryMenu.setTabs('MediaCleaner', commons.TabsTroubleshooting, commons.getTabs)
    Dashboard.showLoadingMsg()

    const $TroubleshootingButtonCopy = page.querySelector('#TroubleshootingButtonCopy')
    $TroubleshootingButtonCopy.addEventListener('click', troubleshootingButtonCopyClick)

    const $TroubleshootingButtonGetLog = page.querySelector('#TroubleshootingButtonGetLog')
    $TroubleshootingButtonGetLog.addEventListener('click', troubleshootingButtonGetLogClick)

    getLog(page)
}

function getLog(page) {
    const request = {
        url: ApiClient.getUrl('MediaCleaner/Log'),
    }

    Dashboard.showLoadingMsg()
    ApiClient.fetch(request).then(function (result) {
        const log = page.querySelector(logTextareaSelector)
        log.value = result
        Dashboard.hideLoadingMsg()
    }).catch(function (error) {
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Error loading log')
    })
}

function troubleshootingButtonCopyClick(event) {
    const page = document.querySelector(pageSelector)
    const log = page.querySelector(logTextareaSelector)

    try {
        navigator.clipboard.writeText(log.value)
        Dashboard.alert('Log copied to clipboard')
    } catch {
        // todo
    }
}

function troubleshootingButtonGetLogClick(event) {
    const page = document.querySelector(pageSelector)
    getLog(page)
}
