export default function (view, params) {
    const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

    view.addEventListener('viewshow', function (e) {
        import(commonsUrl).then(onViewShow.bind(this))
    })

    view.querySelector('#LeavingSoonRefresh').addEventListener('click', function (e) {
        loadLeavingSoon(view)
    })
}

function onViewShow(commons) {
    const page = this
    commons.setTabs('MediaCleaner', commons.TabLeavingSoon, commons.getTabs)
    loadLeavingSoon(page)
}

function loadLeavingSoon(page) {
    Dashboard.showLoadingMsg()

    ApiClient.fetch({
        type: 'GET',
        url: ApiClient.getUrl('MediaCleaner/LeavingSoon'),
        dataType: 'json'
    }).then(response => {
        renderLeavingSoon(page, parseResponse(response))
        Dashboard.hideLoadingMsg()
    }).catch(error => {
        console.log(error)
        Dashboard.hideLoadingMsg()
        Dashboard.alert('Error loading Leaving Soon')
    })
}

function renderLeavingSoon(page, response) {
    const items = getValue(response, 'Items') || []
    const collectionExists = getValue(response, 'CollectionExists') === true
    const totalCount = getValue(response, 'TotalCount') ?? items.length
    const summary = page.querySelector('#LeavingSoonSummary')
    const empty = page.querySelector('#LeavingSoonEmpty')
    const table = page.querySelector('#LeavingSoonTable')
    const body = page.querySelector('#LeavingSoonItems')

    summary.textContent = `${totalCount} ${totalCount === 1 ? 'item' : 'items'}`
    body.innerHTML = ''

    if (!collectionExists) {
        empty.textContent = 'Collection not found.'
        empty.style.display = 'block'
        table.style.display = 'none'
        return
    }

    if (items.length === 0) {
        empty.textContent = 'Collection is empty.'
        empty.style.display = 'block'
        table.style.display = 'none'
        return
    }

    empty.style.display = 'none'
    table.style.display = 'table'

    items.forEach(item => {
        const row = document.createElement('tr')
        appendCell(row, getValue(item, 'Name'))
        appendCell(row, getValue(item, 'Type'))
        appendCell(row, getValue(item, 'SeriesName'))
        appendCell(row, getValue(item, 'SeasonName'))
        appendCell(row, getValue(item, 'ProductionYear'))
        appendCell(row, formatDate(getValue(item, 'DateCreated')))
        body.appendChild(row)
    })
}

function appendCell(row, value) {
    const cell = document.createElement('td')
    cell.textContent = value || ''
    row.appendChild(cell)
}

function parseResponse(response) {
    if (typeof response === 'string') {
        return JSON.parse(response)
    }

    return response || {}
}

function getValue(object, name) {
    if (!object) {
        return null
    }

    const camelCaseName = name.charAt(0).toLowerCase() + name.slice(1)
    return object[name] ?? object[camelCaseName]
}

function formatDate(value) {
    if (!value) {
        return ''
    }

    const date = new Date(value)
    if (Number.isNaN(date.getTime())) {
        return ''
    }

    return date.toLocaleDateString()
}
