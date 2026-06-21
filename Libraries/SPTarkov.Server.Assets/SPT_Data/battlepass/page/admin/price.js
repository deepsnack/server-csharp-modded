'use strict';
/*
 * 通行证统一价格 / 货币组件（约定组件，配合 picker.js 使用）
 * ------------------------------------------------------------------
 * 目标：所有涉及「价格 / 成本 / 货币」的管理模块都走这里，避免各页各写一套：
 *   - 货币与以物易物物品统一通过 BpPicker 搜索，不再手填 tpl；
 *   - 每个支付项使用「物品 + 数量」表单，可组合多种支付物品；
 *   - 序列化形状与后端保持一致：cost = [{ tpl, count }]，后端无需改动。
 *
 * 扩展性：新增一种游戏货币时，只需在下方 CURRENCIES 数组加一行
 *   （tpl 需与后端 Money / CurrencyType 一致），所有调用方自动获得该选项。
 *
 * 用法：
 *   // 1) 主货币下拉（如商人主货币）：option 的 value 为货币 code（RUB/USD/EUR/GP）
 *   BpPrice.fillCurrencySelect(selectEl, 'RUB');
 *
 *   // 2) 价格编辑器（货架价 / 跳蚤价 / 配方原料…）：
 *   const ed = BpPrice.createEditor(containerEl);
 *   ed.setCost(existingCostArray);          // [{tpl,count}] 回填
 *   const { cost, invalid } = ed.collect(); // 收集；cost: [{tpl,count}]
 */
(function (global) {
    // —— 唯一货币清单（单一可信源；新增货币只需在此加一行）——
    // tpl 须与后端 SPTarkov.Server.Core.Models.Enums.Money 一致。
    const CURRENCIES = [
        { code: 'RUB', tpl: '5449016a4bdc2d6f028b456f', label: '卢布 RUB' },
        { code: 'USD', tpl: '5696686a4bdc2da3298b456a', label: '美元 USD' },
        { code: 'EUR', tpl: '569668774bdc2da2298b4568', label: '欧元 EUR' },
        { code: 'GP', tpl: '5d235b4d86f7742e017bc88a', label: 'GP 币' },
    ];
    const BY_TPL = {}, BY_CODE = {};
    CURRENCIES.forEach(c => { BY_TPL[c.tpl] = c; BY_CODE[c.code] = c; });

    const ICON_API = '/battlepass/api/icons/';
    function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }
    function esc(s) { return (s == null ? '' : String(s)).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

    function currencyByTpl(tpl) { return BY_TPL[(tpl || '').trim()] || null; }
    function currencyByCode(code) { return BY_CODE[(code || '').trim().toUpperCase()] || null; }
    function isCurrency(tpl) { return !!currencyByTpl(tpl); }

    // 填充一个「主货币」下拉（value=code）
    function fillCurrencySelect(sel, selectedCode) {
        if (!sel) return;
        sel.innerHTML = CURRENCIES.map(c => `<option value="${c.code}">${esc(c.label)}</option>`).join('');
        if (selectedCode && BY_CODE[selectedCode]) sel.value = selectedCode;
    }

    // —— 价格编辑器：所有货币 / 以物易物物品统一走 BpPicker 搜索 + 数量表单 ——
    function createEditor(container) {
        container.classList.add('price-editor');
        container.innerHTML = '';
        const rowsBox = document.createElement('div'); rowsBox.className = 'price-rows';
        const addBtn = document.createElement('button');
        addBtn.type = 'button'; addBtn.className = 'btn ghost small price-add';
        addBtn.textContent = '+ 添加价格项';
        container.appendChild(rowsBox); container.appendChild(addBtn);

        function addRow(entry) {
            entry = entry || {};
            const tpl = (entry.tpl || '').trim();
            const currency = currencyByTpl(tpl);
            const row = document.createElement('div'); row.className = 'price-row';
            row.innerHTML =
                `<div class="price-item-wrap tpl-row">
                     <input class="price-item-input" placeholder="搜索支付物品或货币名称 / tpl…" autocomplete="off" value="${esc(currency ? currency.label : tpl)}" />
                     <img class="icon-preview price-item-icon" alt=""${tpl ? ` src="${ICON_API}${esc(tpl)}"` : ' style="display:none"'} />
                     <div class="price-item-results rw-tpl-results" style="display:none"></div>
                 </div>
                 <input class="price-amt" type="number" min="1" step="1" placeholder="数量" value="${entry.count > 0 ? entry.count : 1}" />
                 <button type="button" class="mini del price-del" title="移除">×</button>`;
            rowsBox.appendChild(row);

            const itemInput = row.querySelector('.price-item-input');
            const itemIcon = row.querySelector('.price-item-icon');
            const itemResults = row.querySelector('.price-item-results');
            if (tpl) row.dataset.itemTpl = tpl;

            if (global.BpPicker) {
                global.BpPicker.attachItem(itemInput, itemResults, ds => {
                    itemInput.value = ds.tpl; row.dataset.itemTpl = ds.tpl;
                    itemIcon.src = ICON_API + ds.tpl; itemIcon.style.display = '';
                });
            }
            itemInput.addEventListener('input', () => {
                const v = itemInput.value.trim();
                if (isTpl(v)) { row.dataset.itemTpl = v; itemIcon.src = ICON_API + v; itemIcon.style.display = ''; }
                else { delete row.dataset.itemTpl; itemIcon.style.display = 'none'; }
            });
            row.querySelector('.price-del').onclick = () => row.remove();
            return row;
        }

        addBtn.onclick = () => addRow();

        return {
            element: container,
            addRow,
            clear() { rowsBox.innerHTML = ''; },
            setCost(cost) { rowsBox.innerHTML = ''; (cost || []).forEach(c => addRow(c)); },
            // 收集所有行 → { cost:[{tpl,count}], invalid }
            collect() {
                const cost = []; let invalid = false;
                rowsBox.querySelectorAll('.price-row').forEach(row => {
                    const count = parseInt(row.querySelector('.price-amt').value, 10);
                    const tpl = (row.dataset.itemTpl || row.querySelector('.price-item-input').value || '').trim();
                    if (!isTpl(tpl)) { invalid = true; return; }
                    if (!(count > 0)) { invalid = true; return; }
                    cost.push({ tpl, count });
                });
                return { cost, invalid };
            },
            getCost() { return this.collect().cost; },
        };
    }

    global.BpPrice = {
        CURRENCIES,
        currencyByTpl, currencyByCode, isCurrency,
        fillCurrencySelect,
        createEditor,
    };
})(window);
