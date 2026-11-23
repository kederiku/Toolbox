(function (expectedOrigin) {
    'use strict';
    if (window !== window.top || location.origin !== expectedOrigin || !/^https?:$/.test(location.protocol)) return null;

    const rows = [];
    let length = 0, inspected = 0, visited = 0, overflow = false;
    const limit = 120000, maxNodes = 180000;
    const mac = /\b(?:[0-9a-f]{2}([:-]))(?:[0-9a-f]{2}\1){4}[0-9a-f]{2}\b|\b[0-9a-f]{4}\.[0-9a-f]{4}\.[0-9a-f]{4}\b|\b[0-9a-f]{12}\b/i;
    const ip = /\b(?:\d{1,3}\.){3}\d{1,3}\b|(?:^|\s|[\[(])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.]{0,39}(?=$|[\s%\])])/i;
    const nameHeader = /\b(?:nom|name|hostname|host name|appareil|device|périphérique|équipement|client)\b/i;
    const ipHeader = /\b(?:ipv?[46]?|adresse internet|internet address)\b/i;
    const sensitive = /mot de passe|password|passphrase|secret|security key|clé de sécurité|access token|jeton|private key|clé privée/i;
    const omitted = 'input,textarea,select,option,button,script,style,noscript,template,[hidden],[aria-hidden="true"],[contenteditable]:not([contenteditable="false"])';
    const tableSelector = 'table,[role="table"],[role="grid"],[role="treegrid"]';
    const rowSelector = 'tr,[role="row"]';

    function visible(element) {
        if (!element || element.closest(omitted)) return false;
        const win = element.ownerDocument.defaultView;
        if (!win || !element.getClientRects().length) return false;
        if (element.checkVisibility)
            return element.checkVisibility({ opacityProperty: true, visibilityProperty: true, checkOpacity: true, checkVisibilityCSS: true });
        const style = win.getComputedStyle(element);
        return style.visibility === 'visible' && style.opacity !== '0';
    }

    function inPrivateForm(element) {
        const form = element.closest('form');
        return !!(form && form.querySelector('input[type="password"],input[autocomplete="one-time-code"]'));
    }

    // Read rendered text nodes only. Form controls, editable content and hidden nodes contribute no text.
    function lines(element) {
        const parts = [];
        function walk(node) {
            if (++visited > maxNodes || parts.length >= 100) return;
            if (node.nodeType === 3) {
                const value = node.nodeValue.replace(/\s+/g, ' ').trim().slice(0, 256);
                if (value) parts.push(value);
            } else if (node.nodeType === 1 && visible(node) && !inPrivateForm(node)) {
                for (let child = node.firstChild; child; child = child.nextSibling) {
                    walk(child);
                    if (parts.length >= 100 || visited > maxNodes) break;
                }
            }
        }
        walk(element);
        return parts;
    }

    function field(value) {
        const text = value.slice(0, 256);
        return /["\t\r\n]/.test(text) ? '"' + text.replace(/"/g, '""') + '"' : text;
    }

    function add(line) {
        if (length + line.length + 1 > limit || rows.length >= 2200) { overflow = true; return false; }
        rows.push(line);
        length += line.length + 1;
        return true;
    }

    // Match complete field captions, so e.g. the gateway MAC is not a client MAC.
    function fieldLabel(text) {
        if (text.length > 80) return '';
        const key = text.normalize('NFD').toLowerCase().replace(/[^a-z0-9]/g, '');
        if (/^(?:name|nom|hostname|nomdhote|nomhote|host|devicename|clientname|friendlyname|alias|device|appareil|equipement|client|peripherique|nom(?:dequipement|delappareil|appareil|delequipement|duperipherique|duclient|delhote|peripherique))$/.test(key)) return 'Nom';
        if (/^(?:mac|macaddress|addressmac|adressemac|clientmac|hardwareaddress|physicaladdress|adressephysique|adressematerielle)$/.test(key)) return 'MAC';
        if (/^(?:ip(?:v[46])?(?:address)?|adresseip(?:v[46])?|clientip|adresseinternet|internetaddress)$/.test(key)) return 'IP';
        if (/^(?:status|state|etat|statut|online|enligne|connected|connecte|active|actif|connection(?:status|state)?|connexion|etatdeconnexion|statutdeconnexion)$/.test(key)) return 'Statut';
        return '';
    }

    function pairRecord(pairs) {
        if (!pairs.some(p => p[0] === 'MAC' && mac.test(p[1])) &&
            !(pairs.some(p => p[0] === 'Nom') && pairs.some(p => p[0] === 'IP' && ip.test(p[1])))) return null;
        return [pairs.map(p => field(p[0])).join('\t'), pairs.map(p => field(p[1])).join('\t')];
    }

    function addRecord(record) {
        if (!record) return;
        if (rows.length && !add('')) return;
        for (const line of record) if (!add(line)) return;
    }

    function collectTable(table) {
        let header = null, writtenHeader = false, hasRecords = false;
        let vertical = [];
        for (const row of table.querySelectorAll(rowSelector)) {
            if (++inspected > 4500 || visited > maxNodes || length >= limit) break;
            if (row.closest(tableSelector) !== table || !visible(row) || inPrivateForm(row)) continue;
            const cells = Array.from(row.querySelectorAll('th,td,[role="cell"],[role="columnheader"],[role="rowheader"],[role="gridcell"]'))
                .filter(cell => cell.closest(rowSelector) === row && visible(cell)).slice(0, 32);
            if (!cells.length || (cells.length === 1 && cells[0].querySelector(tableSelector))) continue;
            const structuralHeader = cells.every(c => c.matches('th,[role="columnheader"]'));
            const values = cells.map(c => lines(c).join(' ').slice(0, 256));
            const joined = values.join(' ');
            if (!header && cells.length === 2 && cells[0].matches('th,[role="rowheader"]') &&
                !cells[1].matches('th,[role="columnheader"]') && (fieldLabel(values[0]) || vertical.length || sensitive.test(values[0]))) {
                if (sensitive.test(joined)) { vertical = []; return; }
                const label = fieldLabel(values[0]);
                if ((label === 'Nom' || label === 'MAC') && vertical.some(p => p[0] === label)) {
                    addRecord(pairRecord(vertical));
                    vertical = [];
                }
                if (label) vertical.push([label, values[1]]);
                continue;
            }
            if (vertical.length) { addRecord(pairRecord(vertical)); vertical = []; }
            if (structuralHeader || (!mac.test(joined) && !ip.test(joined) && values.some(v => /\bMAC\b/i.test(v) || ipHeader.test(v)))) {
                if (values.some(v => sensitive.test(v))) return;
                header = values;
                writtenHeader = false;
                continue;
            }
            if (sensitive.test(joined) || (!mac.test(joined) && !(header && header.some(v => nameHeader.test(v)) &&
                header.some(v => ipHeader.test(v)) && ip.test(joined)))) continue;
            if (!hasRecords && rows.length && !add('')) break;
            hasRecords = true;
            if (header && !writtenHeader) {
                if (!add(header.map(field).join('\t'))) break;
                writtenHeader = true;
            }
            if (!add(values.map(field).join('\t'))) break;
        }
        addRecord(pairRecord(vertical));
    }

    function cardPairs(values) {
        const pairs = [];
        for (let i = 0; i < values.length; i++) {
            const inline = /^([^:=]{1,80})\s*[:=]\s*(.+)$/.exec(values[i]);
            const label = fieldLabel(inline ? inline[1] : values[i]);
            if (!label) continue;
            if (inline) pairs.push([label, inline[2]]);
            else if (i + 1 < values.length && (!fieldLabel(values[i + 1]) ||
                (label === 'Statut' && /^(?:online|active|connected|en ligne|actif|connecté)$/i.test(values[i + 1]))))
                pairs.push([label, values[++i]]);
        }
        return pairs;
    }

    function collectCards(doc) {
        const selector = 'li,article,dl,[role="listitem"],[class*="device" i],[class*="client" i],[class*="host" i],[class*="appareil" i],.card,.item,.row,.list-group-item';
        const container = '[role="list"],[class*="device" i],[class*="client" i],[class*="host" i],[class*="appareil" i],[id*="device" i],[id*="client" i],[id*="host" i],[id*="appareil" i]';
        const cards = [], covered = new WeakSet();
        for (const item of doc.querySelectorAll(selector)) {
            if (cards.length >= 2500) { overflow = true; break; }
            const named = /(?:^|[\s_-])(?:device|client|host|appareil)(?:[\s_-](?:item|row|card|entry))?(?:$|\s)|(?:client|device)(?:Item|Card|Row)(?:$|\s)/i;
            if (item.matches('li,article,dl,[role="listitem"]') || named.test(item.getAttribute('class') || '') ||
                (item.matches('.card,.item,.row,.list-group-item') && item.parentElement && item.parentElement.closest(container))) cards.push(item);
        }
        // Prefer the innermost matching record, while preserving the order displayed by the router.
        const records = [], candidates = new Set(cards);
        for (let i = cards.length - 1; i >= 0; i--) {
            const card = cards[i];
            if (++inspected > 4500 || visited > maxNodes) break;
            if (covered.has(card) || card.closest(tableSelector) || card.querySelector(tableSelector) || !visible(card) || inPrivateForm(card)) continue;
            const values = lines(card);
            if (values.length < 2 || values.length >= 100 || !mac.test(values.join(' ')) || values.some(v => sensitive.test(v))) continue;
            let pairs = [];
            if (card.matches('dl')) {
                for (const label of card.querySelectorAll('dt')) {
                    const value = label.nextElementSibling;
                    if (value && value.matches('dd') && visible(label) && visible(value)) {
                        const caption = fieldLabel(lines(label).join(' '));
                        if (caption) pairs.push([caption, lines(value).join(' ')]);
                    }
                }
            }
            if (!pairs.length) pairs = cardPairs(values);
            // In a card composed of .row elements, the MAC row alone is not the whole device.
            if (pairs.length === 1 && pairs[0][0] === 'MAC') {
                let parent = card.parentElement;
                while (parent && !candidates.has(parent)) parent = parent.parentElement;
                if (parent) continue;
            }
            records.push(pairRecord(pairs) || [values.map(field).join('\t')]);
            for (let parent = card.parentElement; parent; parent = parent.parentElement) covered.add(parent);
        }
        for (const record of records.reverse()) addRecord(record);
    }

    const seen = new Set();
    function collect(doc) {
        if (!doc || doc.location.origin !== expectedOrigin || seen.has(doc) || seen.size >= 12) return;
        seen.add(doc);
        for (const table of doc.querySelectorAll(tableSelector)) {
            if (inspected > 4500 || visited > maxNodes || length >= limit) break;
            if (visible(table) && !inPrivateForm(table)) collectTable(table);
        }
        collectCards(doc);
        for (const frame of doc.querySelectorAll('iframe,frame')) {
            if (seen.size >= 12 || inspected > 4500 || visited > maxNodes) break;
            if (!visible(frame)) continue;
            try { collect(frame.contentDocument); } catch (e) { /* The browser isolates cross-origin frames. */ }
        }
    }
    collect(document);
    // Distinguish an incomplete page from a complete list and an origin mismatch.
    return overflow || inspected > 4500 || visited > maxNodes ? false : rows.join('\n');
})
