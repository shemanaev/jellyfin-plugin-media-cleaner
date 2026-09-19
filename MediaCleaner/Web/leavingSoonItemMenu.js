const marker = 'mediaCleanerLeavingSoonAction'
const itemIdPattern = /^[0-9a-f]{32}$/i
const collectionDeadlineSelector = '.collectionItems .collectionItemsContainer .card[data-id]'
const detailDeadlineSelector = '.itemDetailPage:not(.hide) .itemMiscInfo-primary'
const collectionDeadlineSpacing = '3em'
const actionSheetViewportMargin = 10
const collectionCacheDuration = 30000
let pendingItemId = null
let pendingUntil = 0
let collectionScanTimer = null
let collectionPageId = null
let collectionItems = null
let collectionLoadedAt = 0
let collectionLoadPromise = null
let collectionContextRevision = 0
let detailScanTimer = null
let detailPageId = null
let detailDeadline = null
let detailLoadedAt = 0
let detailLoadPromise = null
let detailContextRevision = 0

document.addEventListener('pointerdown', rememberItem, true)
document.addEventListener('contextmenu', rememberItem, true)
document.addEventListener('click', rememberItem, true)

new MutationObserver(() => {
    injectIntoOpenActionSheets()
    scheduleCollectionDeadlineInjection()
    scheduleDetailDeadlineInjection()
}).observe(document.documentElement, {
    childList: true,
    subtree: true
})

window.addEventListener('hashchange', () => {
    resetCollectionDeadlineContext()
    resetDetailDeadlineContext()
    scheduleCollectionDeadlineInjection()
    scheduleDetailDeadlineInjection()
})

scheduleCollectionDeadlineInjection()
scheduleDetailDeadlineInjection()
setInterval(updateDeadlineText, 60000)

function rememberItem(event) {
    const itemId = findItemId(event.target)
    if (!itemId) {
        if (event.type === 'pointerdown') pendingItemId = null
        return
    }

    pendingItemId = itemId
    pendingUntil = Date.now() + 5000
    queueMicrotask(injectIntoOpenActionSheets)
}

function findItemId(target) {
    if (!(target instanceof Element)) return null
    const source = target.closest('[data-id], [data-itemid], [data-item-id], a[href*="id="]')
    const candidates = source ? [source.dataset.id, source.dataset.itemid, source.dataset.itemId] : []
    if (source instanceof HTMLAnchorElement) {
        try { candidates.push(new URL(source.href, document.baseURI).searchParams.get('id')) } catch (_) { }
    }
    if (target.closest('.btnMoreCommands')) {
        const hashQuery = window.location.hash.split('?')[1] || ''
        candidates.push(new URLSearchParams(hashQuery).get('id'), new URLSearchParams(window.location.search).get('id'))
    }

    return candidates.find(value => itemIdPattern.test(value || '')) || null
}

function injectIntoOpenActionSheets() {
    if (!pendingItemId || Date.now() > pendingUntil) return
    const containers = new Set()
    document.querySelectorAll('.actionSheetContent').forEach(sheet => {
        containers.add(sheet.querySelector('.actionSheetScroller') || sheet)
    })

    containers.forEach(container => inject(container, pendingItemId))
}

function inject(container, itemId) {
    const itemMarker = `:${itemId}`
    if ((container.dataset[marker] || '').endsWith(itemMarker)) return
    restoreOrRemoveInjectedAction(container)
    container.dataset[marker] = `loading${itemMarker}`

    ApiClient.fetch({
        url: ApiClient.getUrl(`MediaCleaner/LeavingSoon/${encodeURIComponent(itemId)}/Protection`),
        dataType: 'json'
    }).then(response => response.json ? response.json() : response).then(data => {
        if (container.dataset[marker] !== `loading${itemMarker}` || !container.isConnected) return
        if (!read(data, 'ShowAction', 'showAction')) {
            container.dataset[marker] = `none${itemMarker}`
            return
        }

        const protectedByUser = Boolean(read(data, 'IsProtected', 'isProtected'))
        const canProtect = Boolean(read(data, 'CanProtect', 'canProtect'))
        const canRemove = Boolean(read(data, 'CanRemove', 'canRemove'))
        const protectedTargetCount = Number(read(data, 'ProtectedTargetCount', 'protectedTargetCount') || 0)
        const primaryRemoves = protectedByUser || (!canProtect && canRemove)
        const ownedCollectionId = normalizeItemId(read(data, 'CollectionId', 'collectionId'))
        const nativeRemove = ownedCollectionId === currentDetailsItemId()
            ? container.querySelector('[data-id="removefromcollection"]')
            : null
        const button = nativeRemove ? nativeRemove.cloneNode(false) : document.createElement('button')
        if (nativeRemove) {
            button.removeAttribute('data-id')
            button.dataset.mediaCleanerReplacedRemove = 'true'
            button.mediaCleanerOriginalButton = nativeRemove.cloneNode(true)
            nativeRemove.replaceWith(button)
        } else {
            button.type = 'button'
            button.setAttribute('is', 'emby-button')
            button.setAttribute('role', 'menuitem')
            button.className = 'listItem listItem-button actionSheetMenuItem emby-button'
        }
        button.dataset.mediaCleanerLeavingSoonAction = 'true'

        const iconName = primaryRemoves ? 'delete' : 'bookmark'
        const icon = document.createElement('span')
        icon.className = `actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ${iconName}`
        icon.setAttribute('aria-hidden', 'true')
        button.appendChild(icon)

        const body = document.createElement('div')
        body.className = 'listItemBody actionsheetListItemBody'
        const label = document.createElement('div')
        label.className = 'listItemBodyText actionSheetItemText'
        label.textContent = primaryRemoves
            ? 'Remove keep until watched'
            : protectedTargetCount > 0 ? 'Keep remaining until watched' : 'Keep until watched'
        body.appendChild(label)
        if (!primaryRemoves && !canProtect) {
            const hint = document.createElement('div')
            hint.className = 'listItemBodyText secondary'
            hint.textContent = 'Mark unplayed first'
            body.appendChild(hint)
        }
        button.appendChild(body)
        button.addEventListener('click', () => {
            if (!primaryRemoves && !canProtect) {
                Dashboard.alert('Mark this item unplayed in Jellyfin before saving it until watched.')
                return
            }
            changeProtection(button, itemId, primaryRemoves)
        })
        if (!nativeRemove) container.appendChild(button)
        if (!protectedByUser && canProtect && canRemove) {
            const removeButton = document.createElement('button')
            removeButton.type = 'button'
            removeButton.setAttribute('is', 'emby-button')
            removeButton.setAttribute('role', 'menuitem')
            removeButton.className = 'listItem listItem-button actionSheetMenuItem emby-button'
            removeButton.dataset.mediaCleanerLeavingSoonAction = 'true'
            const removeIcon = document.createElement('span')
            removeIcon.className = 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons delete'
            removeIcon.setAttribute('aria-hidden', 'true')
            removeButton.appendChild(removeIcon)
            const removeBody = document.createElement('div')
            removeBody.className = 'listItemBody actionsheetListItemBody'
            const removeLabel = document.createElement('div')
            removeLabel.className = 'listItemBodyText actionSheetItemText'
            removeLabel.textContent = 'Remove keep until watched'
            removeBody.appendChild(removeLabel)
            removeButton.appendChild(removeBody)
            removeButton.addEventListener('click', () => changeProtection(removeButton, itemId, true))
            container.appendChild(removeButton)
        }
        container.dataset[marker] = `ready${itemMarker}`
        if (!nativeRemove || (!protectedByUser && canProtect && canRemove)) scheduleActionSheetViewportFit(container)
    }).catch(error => {
        console.debug('Media Cleaner could not inspect the selected item.', error)
        if (container.dataset[marker] === `loading${itemMarker}`) container.dataset[marker] = `error${itemMarker}`
    })
}

function restoreOrRemoveInjectedAction(container) {
    container.querySelectorAll('[data-media-cleaner-leaving-soon-action]').forEach(previous => {
        if (previous.dataset.mediaCleanerReplacedRemove === 'true' && previous.mediaCleanerOriginalButton) {
            previous.replaceWith(previous.mediaCleanerOriginalButton)
            return
        }
        previous.remove()
    })
}

function scheduleActionSheetViewportFit(container) {
    const dialog = container.closest('.actionSheet')
    if (!dialog || dialog.classList.contains('actionsheet-fullscreen')) return
    requestAnimationFrame(() => fitActionSheetToViewport(dialog))
    setTimeout(() => fitActionSheetToViewport(dialog), 180)
}

function fitActionSheetToViewport(dialog) {
    if (!dialog.isConnected) return
    const scroller = dialog.querySelector('.actionSheetScroller')
    if (!scroller) return

    if (scroller.dataset.mediaCleanerViewportFit) {
        scroller.style.removeProperty('max-height')
        delete scroller.dataset.mediaCleanerViewportFit
    }

    const viewportHeight = document.documentElement.clientHeight || window.innerHeight
    const viewportWidth = document.documentElement.clientWidth || window.innerWidth
    const maximumDialogHeight = viewportHeight - actionSheetViewportMargin * 2
    let dialogRect = dialog.getBoundingClientRect()
    if (dialogRect.height > maximumDialogHeight) {
        const scrollerRect = scroller.getBoundingClientRect()
        const fixedHeight = Math.max(0, dialogRect.height - scrollerRect.height)
        const maximumScrollerHeight = Math.max(96, maximumDialogHeight - fixedHeight)
        scroller.style.setProperty('max-height', `${maximumScrollerHeight}px`)
        scroller.dataset.mediaCleanerViewportFit = 'true'
        dialogRect = dialog.getBoundingClientRect()
    }

    let top = Number.parseFloat(dialog.style.top)
    let left = Number.parseFloat(dialog.style.left)
    if (!Number.isFinite(top)) top = dialogRect.top
    if (!Number.isFinite(left)) left = dialogRect.left

    const bottomOverflow = dialogRect.bottom - (viewportHeight - actionSheetViewportMargin)
    if (bottomOverflow > 0) top -= bottomOverflow
    const rightOverflow = dialogRect.right - (viewportWidth - actionSheetViewportMargin)
    if (rightOverflow > 0) left -= rightOverflow
    if (dialogRect.top < actionSheetViewportMargin) top += actionSheetViewportMargin - dialogRect.top
    if (dialogRect.left < actionSheetViewportMargin) left += actionSheetViewportMargin - dialogRect.left

    dialog.style.top = `${Math.max(actionSheetViewportMargin, top)}px`
    dialog.style.left = `${Math.max(actionSheetViewportMargin, left)}px`
}

function changeProtection(button, itemId, protectedByUser) {
    button.disabled = true
    const request = protectedByUser
        ? { type: 'DELETE', url: ApiClient.getUrl(`MediaCleaner/LeavingSoon/Protections/Mine/Items/${encodeURIComponent(itemId)}`) }
        : { type: 'PUT', url: ApiClient.getUrl(`MediaCleaner/LeavingSoon/${encodeURIComponent(itemId)}/Protection`) }

    ApiClient.fetch(request).then(() => {
        button.remove()
        resetCollectionDeadlineContext()
        resetDetailDeadlineContext()
        scheduleCollectionDeadlineInjection()
        scheduleDetailDeadlineInjection()
        if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
            Dashboard.alert(protectedByUser ? 'Keep until watched removed.' : 'This item will be kept until you watch it.')
        }
    }).catch(error => {
        console.error(error)
        button.disabled = false
        if (typeof Dashboard !== 'undefined' && Dashboard.alert) Dashboard.alert('The protection could not be changed. Try again.')
    })
}

function scheduleCollectionDeadlineInjection() {
    if (collectionScanTimer != null) return
    collectionScanTimer = setTimeout(() => {
        collectionScanTimer = null
        injectCollectionDeadlines()
    }, 50)
}

function injectCollectionDeadlines() {
    if (!document.querySelector(collectionDeadlineSelector)) return
    const pageId = currentDetailsItemId()
    if (!pageId) return

    if (collectionPageId !== pageId) resetCollectionDeadlineContext(pageId)
    if (collectionItems && Date.now() - collectionLoadedAt <= collectionCacheDuration) {
        renderCollectionDeadlines(collectionItems)
        return
    }
    if (collectionLoadPromise) return

    const revision = collectionContextRevision
    collectionLoadPromise = ApiClient.fetch({
        url: ApiClient.getUrl(`MediaCleaner/LeavingSoon/Collections/${encodeURIComponent(pageId)}/Cards`),
        dataType: 'json'
    }).then(response => response.json ? response.json() : response).then(data => {
        if (collectionPageId !== pageId || collectionContextRevision !== revision) return
        collectionItems = new Map()
        if (read(data, 'IsOwned', 'isOwned')) {
            const items = read(data, 'Items', 'items') || []
            items.forEach(item => {
                const itemId = normalizeItemId(read(item, 'ItemId', 'itemId'))
                if (itemId) collectionItems.set(itemId, item)
            })
        }
        collectionLoadedAt = Date.now()
        renderCollectionDeadlines(collectionItems)
    }).catch(error => {
        console.debug('Media Cleaner could not load Leaving Soon deadlines.', error)
    }).finally(() => {
        if (collectionPageId === pageId && collectionContextRevision === revision) collectionLoadPromise = null
    })
}

function renderCollectionDeadlines(items) {
    document.querySelectorAll(collectionDeadlineSelector).forEach(card => {
        const item = items.get(normalizeItemId(card.dataset.id))
        const existing = card.querySelector('[data-media-cleaner-leaving-soon-deadline]')
        const cardBox = card.querySelector('.cardBox-bottompadded')
        if (!item) {
            existing?.remove()
            clearCollectionDeadlineSpacing(cardBox)
            return
        }

        const footer = card.querySelector('.cardFooter') || cardBox
        if (!footer) return
        const presentation = deadlinePresentation(item)
        const line = existing || document.createElement('div')
        line.dataset.mediaCleanerLeavingSoonDeadline = 'true'
        line.className = `cardText cardText-secondary mediaCleanerLeavingSoonDeadline${card.querySelector('.cardTextCentered') ? ' cardTextCentered' : ''}`
        if (line.textContent !== presentation.text) line.textContent = presentation.text
        if (presentation.title && line.title !== presentation.title) line.title = presentation.title
        else if (!presentation.title && line.hasAttribute('title')) line.removeAttribute('title')
        if (!existing) footer.appendChild(line)
        if (cardBox) {
            cardBox.dataset.mediaCleanerLeavingSoonSpacing = 'true'
            cardBox.style.setProperty('margin-bottom', collectionDeadlineSpacing, 'important')
        }
    })
}

function clearCollectionDeadlineSpacing(cardBox) {
    if (!cardBox?.dataset.mediaCleanerLeavingSoonSpacing) return
    cardBox.style.removeProperty('margin-bottom')
    delete cardBox.dataset.mediaCleanerLeavingSoonSpacing
}

function updateCollectionDeadlineText() {
    if (collectionItems) renderCollectionDeadlines(collectionItems)
}

function scheduleDetailDeadlineInjection() {
    if (detailScanTimer != null) return
    detailScanTimer = setTimeout(() => {
        detailScanTimer = null
        injectDetailDeadline()
    }, 50)
}

function injectDetailDeadline() {
    const pageId = currentDetailsItemId()
    const container = document.querySelector(detailDeadlineSelector)
    if (!pageId || !container) return

    if (detailPageId !== pageId) resetDetailDeadlineContext(pageId)
    if (detailLoadedAt && Date.now() - detailLoadedAt <= collectionCacheDuration) {
        renderDetailDeadline(detailDeadline)
        return
    }
    if (detailLoadPromise) return

    const revision = detailContextRevision
    detailLoadPromise = ApiClient.fetch({
        url: ApiClient.getUrl(`MediaCleaner/LeavingSoon/${encodeURIComponent(pageId)}/Protection`),
        dataType: 'json'
    }).then(response => response.json ? response.json() : response).then(data => {
        if (detailPageId !== pageId || detailContextRevision !== revision) return
        detailDeadline = read(data, 'ShowAction', 'showAction') ? data : null
        detailLoadedAt = Date.now()
        renderDetailDeadline(detailDeadline)
    }).catch(error => {
        console.debug('Media Cleaner could not load the item deadline.', error)
    }).finally(() => {
        if (detailPageId === pageId && detailContextRevision === revision) detailLoadPromise = null
    })
}

function renderDetailDeadline(item) {
    const container = document.querySelector(detailDeadlineSelector)
    if (!container) return
    const existing = container.querySelector('[data-media-cleaner-leaving-soon-detail-deadline]')
    const presentation = item ? detailDeadlinePresentation(item) : null
    if (!presentation) {
        existing?.remove()
        return
    }

    const line = existing || document.createElement('div')
    line.dataset.mediaCleanerLeavingSoonDetailDeadline = 'true'
    line.className = 'mediaInfoItem'
    if (line.textContent !== presentation.text) line.textContent = presentation.text
    if (presentation.title && line.title !== presentation.title) line.title = presentation.title
    else if (!presentation.title && line.hasAttribute('title')) line.removeAttribute('title')
    if (!existing) container.appendChild(line)
}

function detailDeadlinePresentation(item) {
    const status = String(read(item, 'Status', 'status') || '')
    if (status === 'ProtectedUntilWatched') return { text: 'Kept until watched', title: '' }
    if (status === 'ProtectedByOtherUser') return { text: 'Protected by another user', title: '' }
    if (status === 'PendingPublication') return { text: 'Waiting for refresh', title: '' }

    const value = read(item, 'DeleteAfterUtc', 'deleteAfterUtc')
    const deadline = value ? new Date(value) : null
    if (!deadline || Number.isNaN(deadline.getTime())) return status ? { text: 'Waiting for refresh', title: '' } : null
    const remaining = deadline.getTime() - Date.now()
    const title = `Scheduled for deletion after ${deadline.toLocaleString()}`
    if (remaining <= 0 || status === 'Ready') return { text: 'Due now', title }

    const hour = 60 * 60 * 1000
    const day = 24 * hour
    if (remaining > day) return { text: `Deletes in ${Math.ceil(remaining / day)}d`, title }
    if (remaining > hour) return { text: `Deletes in ${Math.ceil(remaining / hour)}h`, title }
    return { text: 'Deletes within 1h', title }
}

function updateDeadlineText() {
    updateCollectionDeadlineText()
    if (detailDeadline) renderDetailDeadline(detailDeadline)
}

function deadlinePresentation(item) {
    const status = String(read(item, 'Status', 'status') || '')
    if (status === 'ProtectedUntilWatched') return { text: 'Kept', title: 'Kept until watched' }
    if (status === 'ProtectedByOtherUser') return { text: 'Protected', title: 'Protected by another user' }
    if (status === 'PendingPublication') return { text: 'Pending', title: 'Waiting for publication' }

    const value = read(item, 'DeleteAfterUtc', 'deleteAfterUtc')
    const deadline = value ? new Date(value) : null
    if (!deadline || Number.isNaN(deadline.getTime())) return { text: 'Pending', title: 'Waiting for publication' }
    const remaining = deadline.getTime() - Date.now()
    const title = `Eligible after ${deadline.toLocaleString()}`
    if (remaining <= 0 || status === 'Ready') return { text: 'Due now', title }

    const hour = 60 * 60 * 1000
    const day = 24 * hour
    if (remaining > day) {
        const days = Math.ceil(remaining / day)
        return { text: `${days}d left`, title }
    }
    if (remaining > hour) {
        const hours = Math.ceil(remaining / hour)
        return { text: `${hours}h left`, title }
    }
    return { text: '<1h left', title }
}

function currentDetailsItemId() {
    const [route, query = ''] = window.location.hash.split('?')
    if (!route.endsWith('/details') && !route.endsWith('/details/')) return null
    return normalizeItemId(new URLSearchParams(query).get('id'))
}

function normalizeItemId(value) {
    const normalized = String(value || '').replaceAll('-', '').toLowerCase()
    return itemIdPattern.test(normalized) ? normalized : null
}

function resetCollectionDeadlineContext(pageId = null) {
    collectionContextRevision++
    collectionPageId = pageId
    collectionItems = null
    collectionLoadedAt = 0
    collectionLoadPromise = null
}

function resetDetailDeadlineContext(pageId = null) {
    detailContextRevision++
    detailPageId = pageId
    detailDeadline = null
    detailLoadedAt = 0
    detailLoadPromise = null
    document.querySelectorAll('[data-media-cleaner-leaving-soon-detail-deadline]').forEach(line => line.remove())
}

function read(value, pascalName, camelName) {
    return value && (Object.prototype.hasOwnProperty.call(value, pascalName) ? value[pascalName] : value[camelName])
}
