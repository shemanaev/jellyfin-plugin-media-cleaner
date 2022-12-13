export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })
}

const pageSelector = '[data-role="page"]'
const logTextareaSelector = '#TroubleshootingLog'
const logViewerSelector = '#TroubleshootingLogViewer'

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

    const $TroubleshootingButtonToggleViewer = page.querySelector('#TroubleshootingButtonToggleViewer')
    $TroubleshootingButtonToggleViewer.addEventListener('click', troubleshootingButtonToggleViewerClick)

    getLog(page)
}

function getLog(page) {
    const request = {
        url: ApiClient.getUrl('MediaCleaner/Log'),
    }

    Dashboard.showLoadingMsg()
    ApiClient.fetch(request).then(function (result) {
        const log = page.querySelector(logTextareaSelector)
        const viewer = page.querySelector(logViewerSelector)
        log.value = result

        const el = document.createElement('html')
        el.innerHTML = result
        const details = el.querySelector('details:last-of-type > pre')
        viewer.innerHTML = ''
        viewer.appendChild(details)

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

function troubleshootingButtonToggleViewerClick(event) {
    const page = this.closest(pageSelector)
    const log = page.querySelector(logTextareaSelector)
    const viewer = page.querySelector(logViewerSelector)

    if (log.style.display === 'none') {
        log.style.display = 'inline'
        viewer.style.display = 'none'
    } else {
        log.style.display = 'none'
        viewer.style.display = 'inline'
    }
}
