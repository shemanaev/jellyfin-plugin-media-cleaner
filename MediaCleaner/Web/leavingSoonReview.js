const pageSize = 50
const commonsUrl = ApiClient.getUrl('web/ConfigurationPage', { name: 'MediaCleaner_commons_js' })

export default function (view) {
    view._mcStart = 0; view._mcGeneration = 0
    view.addEventListener('viewshow', () => import(commonsUrl).then(commons => { commons.setTabs('MediaCleaner', commons.TabLeavingSoonReview, commons.getTabs); load(view) }))
    view.querySelector('#MediaCleanerProtectionRefresh').addEventListener('click', () => load(view))
    view.querySelector('#MediaCleanerProtectionSearch').addEventListener('input', debounce(() => { view._mcStart = 0; load(view) }, 250))
    view.querySelector('#MediaCleanerProtectionPrevious').addEventListener('click', () => { view._mcStart = Math.max(0, view._mcStart - pageSize); load(view) })
    view.querySelector('#MediaCleanerProtectionNext').addEventListener('click', () => { view._mcStart += pageSize; load(view) })
    view.querySelector('#MediaCleanerProtectionList').addEventListener('click', event => action(view, event))
}

function load(page) {
    const generation = ++page._mcGeneration, search = page.querySelector('#MediaCleanerProtectionSearch').value.trim()
    Dashboard.showLoadingMsg(); page.querySelector('#MediaCleanerProtectionError').hidden = true
    ApiClient.fetch({ url: ApiClient.getUrl('MediaCleaner/LeavingSoon/Admin/Protections', { startIndex: page._mcStart, limit: pageSize, search }), dataType: 'json' })
        .then(r => r.json ? r.json() : r).then(data => {
            if (generation !== page._mcGeneration) return
            const total = Number(value(data, 'TotalRecordCount', 'totalRecordCount') || 0)
            if (page._mcStart >= total && page._mcStart > 0) { page._mcStart = Math.max(0, page._mcStart - pageSize); load(page); return }
            render(page, data); Dashboard.hideLoadingMsg()
        }).catch(error => { if (generation !== page._mcGeneration) return; console.error(error); Dashboard.hideLoadingMsg(); const box = page.querySelector('#MediaCleanerProtectionError'); box.textContent = 'Could not load protections. Use Refresh page to try again.'; box.hidden = false })
}

function render(page, data) {
    const items = value(data, 'Items', 'items') || [], total = Number(value(data, 'TotalRecordCount', 'totalRecordCount') || 0)
    const groups = new Map(); items.forEach(item => { const id = value(item, 'NoticeItemId', 'noticeItemId'); if (!groups.has(id)) groups.set(id, []); groups.get(id).push(item) })
    page._mcNoticeNames = new Map([...groups.entries()].map(([id, rows]) => [id, value(rows[0], 'NoticeItemName', 'noticeItemName')]))
    const hasSearch = page.querySelector('#MediaCleanerProtectionSearch').value.trim().length > 0
    const list = page.querySelector('#MediaCleanerProtectionList')
    list.innerHTML = groups.size
        ? [...groups.entries()].map(([id, rows]) => groupCard(id, rows)).join('')
        : emptyState(hasSearch)
    list.querySelectorAll('.mcReviewPoster img').forEach(image => image.addEventListener('error', () => { image.hidden = true }, { once: true }))
    const first = total ? page._mcStart + 1 : 0, last = Math.min(total, page._mcStart + items.length)
    page.querySelector('#MediaCleanerProtectionPageLabel').textContent = `${first}–${last} of ${total}`
    page.querySelector('#MediaCleanerProtectionPrevious').disabled = page._mcStart === 0; page.querySelector('#MediaCleanerProtectionNext').disabled = last >= total
    page.querySelector('.mcReviewPager').hidden = total <= pageSize
}

function groupCard(id, rows) {
    const first = rows[0]
    const noticeName = value(first, 'NoticeItemName', 'noticeItemName')
    const protectionCount = Number(value(first, 'NoticeProtectionCount', 'noticeProtectionCount') || rows.length)
    const title = `<a href="#/details?id=${encodeURIComponent(id)}">${html(noticeName)}</a>`
    const poster = imageUrl(id)
    const people = rows.map(row => {
        const itemName = value(row, 'ItemName', 'itemName')
        const available = Boolean(value(row, 'ItemAvailable', 'itemAvailable'))
        const itemLabel = itemName && itemName !== noticeName ? `${html(itemName)} · ` : ''
        const unavailable = available ? '' : ' · Item unavailable'
        return `<div class="mcReviewPerson">
            <span class="material-icons mcReviewPersonIcon ${available ? 'person' : 'warning'}" aria-hidden="true"></span>
            <div><div class="mcReviewPersonTitle">${html(value(row, 'UserName', 'userName'))}</div>
                <div class="mcReviewPersonMeta">${itemLabel}Saved ${html(formatDate(value(row, 'CreatedAtUtc', 'createdAtUtc')))}${unavailable}</div></div>
            <button is="emby-button" type="button" class="button-flat emby-button mcReviewRemove" data-action="remove" data-id="${attr(value(row, 'ProtectionId', 'protectionId'))}"><span class="material-icons delete" aria-hidden="true"></span><span>Remove</span></button>
        </div>`
    }).join('')
    const removeAll = protectionCount > 1
        ? `<div class="mcReviewCardActions"><button is="emby-button" type="button" class="button-flat emby-button mcReviewRemoveAll" data-action="remove-all" data-item-id="${attr(id)}" data-protection-count="${protectionCount}"><span class="material-icons delete_sweep" aria-hidden="true"></span><span>Remove all ${protectionCount}</span></button></div>`
        : ''
    return `<article class="paperList mcReviewCard">
        <div class="mcReviewPoster"><div class="mcReviewPosterFallback"><span class="material-icons movie" aria-hidden="true"></span></div>${poster ? `<img src="${attr(poster)}" alt="" loading="lazy" />` : ''}</div>
        <div class="mcReviewCardContent">
            <h3 class="mcReviewCardTitle">${title}</h3>
            <div class="mcReviewPeople">${people}</div>
            ${removeAll}
        </div>
    </article>`
}

function emptyState(hasSearch) {
    return `<div class="mcReviewEmpty">${hasSearch ? 'No matching protections.' : 'No active protections.'}</div>`
}

function imageUrl(id) {
    try { return ApiClient.getScaledImageUrl(id, { type: 'Primary', maxWidth: 240, maxHeight: 360, quality: 90 }) } catch (_) { return '' }
}

function action(page, event) {
    const button = event.target.closest('[data-action]'); if (!button) return
    const removeAll = button.dataset.action === 'remove-all'
    const count = Number(button.dataset.protectionCount || 0)
    const itemName = page._mcNoticeNames?.get(button.dataset.itemId) || 'this item'
    const message = removeAll
        ? `Remove all ${count} protections for “${itemName}” across all users? This includes protections hidden by the current search and other pages. A new full Leaving Soon notice will be required.`
        : 'Remove the selected protection? If this is the last one, a new full Leaving Soon notice will be required.'
    if (!confirm(message)) return
    const url = !removeAll
        ? `MediaCleaner/LeavingSoon/Admin/Protections/${button.dataset.id}` : `MediaCleaner/LeavingSoon/Admin/Items/${button.dataset.itemId}/Protections`
    button.disabled = true; ApiClient.fetch({ type: 'DELETE', url: ApiClient.getUrl(url) }).then(() => load(page)).catch(error => { console.error(error); button.disabled = false; Dashboard.alert('The protection changed or could not be removed. Refresh and try again.') })
}

function value(o, p, c) { return o && (Object.prototype.hasOwnProperty.call(o, p) ? o[p] : o[c]) }
function html(v) { const e = document.createElement('div'); e.textContent = v == null ? '' : String(v); return e.innerHTML }
function attr(v) { return html(v).replace(/`/g, '&#96;') }
function formatDate(v) { const d = new Date(v); return Number.isNaN(d.getTime()) ? String(v) : d.toLocaleString() }
function debounce(fn, ms) { let timer; return () => { clearTimeout(timer); timer = setTimeout(fn, ms) } }
