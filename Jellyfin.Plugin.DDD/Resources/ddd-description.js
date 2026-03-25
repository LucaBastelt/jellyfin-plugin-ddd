addEventListeners();
addCssRules();
checkDddUpdateNeeded();


function addEventListeners() {
    addEventListener("hashchange", (event) => {

        console.log("The hash has changed!");
        checkDddUpdateNeeded();
    })
    navigation.addEventListener("navigate", e => {
        console.log("The URL has changed!");
        checkDddUpdateNeeded();
    });
    window.addEventListener('popstate', function (event) {
        console.log("NavState has changed!");
        checkDddUpdateNeeded();
    });
}

function checkDddUpdateNeeded(){
    var path = location.hash.split('?');
    var queryParams = (path[1] ?? "").split('&');
    console.log(path);

    var id = queryParams.find(q => q.startsWith('id='))?.replace('id=', '');

    if (path[0] === '#/details' && id) {
        var request = new XMLHttpRequest();
        request.open("POST", "/plugin/ddd/" + id);

        request.addEventListener('load', function (event) {
            if (request.status >= 200 && request.status < 300) {
                var responseJson = JSON.parse(request.responseText);
                console.log(responseJson);
                patchDescription(responseJson);

                loadDddUrl(id);
            } else {
                console.warn(request.statusText, request.responseText);
            }
        });

        request.send()
    }
}

function loadDddUrl(id){
    var request = new XMLHttpRequest();
    request.open("POST", "/plugin/ddd/" + id + "/dddUrl");

    request.addEventListener('load', function (event) {
        if (request.status >= 200 && request.status < 300) {

            var element = document.querySelector('.itemTags');
            var a = document.createElement('a');
            element.parentElement.insertBefore(a, element);
            a.href = JSON.parse(request.responseText);
            a.innerText = "DoesTheDogDie Website";
            a.target = '_blank';
        } else {
            console.warn(request.statusText, request.responseText);
        }
    });

    request.send()
}

function patchDescription(content) {
    var element = document.querySelector('.itemTags');
    var p = document.createElement('p');
    p.classList.add('ddd-container')
    element.parentElement.insertBefore(p, element);

    p.innerHTML = "<b>Content Warnings: </b>";

    for (const e of content) {
        p.innerHTML += `<span class="ddd-element${e.Comment ? "ddd-has-comment" : ""}" title="${e.Comment}">${e.Name}</span>, `;
    }
    p.innerHTML = p.innerHTML.slice(0, p.innerHTML.length - 2);
}

function addCssRules(){
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
    `

    var styleSheet = document.createElement("style")
    styleSheet.textContent = styles
    document.head.appendChild(styleSheet)
}
