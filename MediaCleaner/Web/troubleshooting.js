export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })
}

const pageSelector = '[data-role="page"]'
const logTextareaSelector = '#TroubleshootingLog'

function onViewShow(commons) {
    const page = this
    LibraryMenu.setTabs('MediaCleaner', commons.TabTroubleshooting, commons.getTabs)
    Dashboard.showLoadingMsg()

    if (window.isSecureContext) {
        const $TroubleshootingButtonCopy = page.querySelector('#TroubleshootingButtonCopy')
        $TroubleshootingButtonCopy.addEventListener('click', troubleshootingButtonCopyClick)
        $TroubleshootingButtonCopy.style.display = 'inline-flex'
    }

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
        console.log(error)
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Error loading log')
    })
}

function troubleshootingButtonCopyClick(event) {
    const page = this.closest(pageSelector)
    const log = page.querySelector(logTextareaSelector)

    navigator.clipboard.writeText(log.value)
        .then(() => {
            Dashboard.alert('Log copied to clipboard')
        })
        .catch(error => {
            console.log('Error copying log', error)
        })
}

function troubleshootingButtonGetLogClick(event) {
    const page = this.closest(pageSelector)
    getLog(page)
}
