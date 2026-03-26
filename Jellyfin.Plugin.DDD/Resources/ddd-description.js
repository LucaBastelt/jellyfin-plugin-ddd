setupOnViewShow();
addCssRules();
checkDddUpdateNeeded();

function checkDddUpdateNeeded() {
    var path = location.hash.split("?");
    var queryParams = (path[1] ?? "").split("&");

    var id = queryParams.find((q) => q.startsWith("id="))?.replace("id=", "");

    if (path[0] === "#/details" && id) {
        var request = new XMLHttpRequest();
        request.open("POST", "/plugin/ddd/" + id);

        request.addEventListener("load", function (event) {
            if (request.status >= 200 && request.status < 300) {
                var responseJson = JSON.parse(request.responseText);
                patchDescription(responseJson);

                loadDddUrl(id);
            } else {
                console.warn(request.statusText, request.responseText);
            }
        });

        request.send();
    }
}

function loadDddUrl(id) {
    var request = new XMLHttpRequest();
    request.open("POST", "/plugin/ddd/" + id + "/dddUrl");

    request.addEventListener("load", function (event) {
        if (request.status >= 200 && request.status < 300) {
            var element = document.querySelector(".itemTags");
            var a = document.createElement("a");
            element.parentElement.insertBefore(a, element);
            a.href = JSON.parse(request.responseText);
            a.innerText = "DoesTheDogDie Website";
            a.target = "_blank";
        } else {
            console.warn(request.statusText, request.responseText);
        }
    });

    request.send();
}

function patchDescription(content) {
    document.querySelectorAll(".ddd-container").forEach(e => e.remove());

    var element = document.querySelector(".itemTags:not(.hide .itemTags)");
    var p = document.createElement("p");
    p.classList.add("ddd-container");
    element.parentElement.insertBefore(p, element);

    p.innerHTML = "<b>Content Warnings: </b>";

    for (const e of content) {
        p.innerHTML += `<span class="ddd-element${e.Comment ? " ddd-has-comment" : ""}" title="${e.Comment}">${e.Name}</span>, `;
    }
    p.innerHTML = p.innerHTML.slice(0, p.innerHTML.length - 2);
}

function addCssRules() {
    var styles = `
        .ddd-container{
            margin-top: 0;
            margin-bottom: 0.5rem;
        }

        .ddd-element.ddd-has-comment::after{
            content: "?";
            font-size: 0.7rem;
            vertical-align: super;
        }
        .ddd-element.ddd-has-comment:hover{
            cursor: help;
            text-decoration: underline;
        }
    `;

    var styleSheet = document.createElement("style");
    styleSheet.textContent = styles;
    document.head.appendChild(styleSheet);
}

// Hook into Emby.Page.onViewShow
function setupOnViewShow() {
    const originalOnViewShow = window.Emby?.Page?.onViewShow;

    if (window.Emby && window.Emby.Page) {
        window.Emby.Page.onViewShow = function (...args) {
            // Call original handler if it exists
            if (originalOnViewShow) {
                try {
                    originalOnViewShow.apply(this, args);
                } catch (err) {
                    console.warn("[DDD] Error in original onViewShow:", err);
                }
            }

            checkDddUpdateNeeded();
        };

        console.log("[DDD] Hooked into Emby.Page.onViewShow");
    } else {
        // Retry if Emby.Page not ready yet
        setTimeout(setupOnViewShow, 100);
    }
}
