namespace LiteShot;

/// <summary>
/// Word lists behind <see cref="NameFinder"/>. All lowercase. The hand-picked lists below come first; the big lists in
/// Names\*.txt (tens of thousands of US, UK, European and Nordic names, built from public data by
/// tools/names/build-name-lists.mjs) are added to them, and where the two disagree the hand-picked lists win.
/// </summary>
internal static class NameLists
{
    static HashSet<string> Set(params string[] groups) =>
        new(groups.SelectMany(g => g.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparer.Ordinal);

    static NameLists()
    {
        // Names that are also words or places only need a surname next to them, unless they're picked by hand below.
        var risky = Resource("first-names-ambiguous.txt");
        Ambiguous.UnionWith(risky.Where(w => !FirstNames.Contains(w)));
        FirstNames.UnionWith(risky);
        FirstNames.UnionWith(Resource("first-names.txt"));
        Surnames.UnionWith(Resource("surnames.txt"));
        Words.UnionWith(Resource("english-words.txt"));
    }

    /// <summary>Everyday English words (lowercase dictionary entries).</summary>
    public static readonly HashSet<string> Words = new(StringComparer.Ordinal);

    /// <summary>True for an everyday English word, or its plural or past tense: "install", "settings", "updated".</summary>
    public static bool IsWord(string w) =>
        Words.Contains(w)
        || (w.EndsWith('s') && Words.Contains(w[..^1]))
        || (w.EndsWith("es") && Words.Contains(w[..^2]))
        || (w.EndsWith("ies") && Words.Contains(w[..^3] + "y"))
        || (w.EndsWith("ed") && (Words.Contains(w[..^2]) || Words.Contains(w[..^1])));

    static List<string> Resource(string file)
    {
        var words = new List<string>();
        using var stream = typeof(NameLists).Assembly.GetManifestResourceStream("LiteShot.Names." + file);
        if (stream == null) return words;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') words.Add(line);
        return words;
    }

    public static readonly HashSet<string> Titles = Set("mr mrs ms miss mx dr prof sir dame madam mme mlle herr frau sr sra srta");

    /// <summary>Common given names from many cultures.</summary>
    public static readonly HashSet<string> FirstNames = Set(
        // English: men
        """
        james john robert michael william david richard joseph thomas charles christopher daniel matthew anthony mark donald
        steven paul andrew joshua kenneth kevin brian george timothy ronald edward jason jeffrey ryan jacob gary nicholas eric
        jonathan stephen larry justin scott brandon benjamin samuel gregory alexander frank patrick raymond jack dennis jerry
        tyler aaron jose adam nathan henry douglas zachary peter kyle ethan walter noah jeremy christian keith roger terry gerald
        harold sean austin carl arthur lawrence dylan jesse bryan billy joe bruce gabriel logan albert willie alan juan
        wayne elijah randy roy vincent ralph eugene russell bobby mason philip louis liam oliver lucas aiden jackson sebastian
        caleb owen luke wyatt isaac levi julian hudson grayson leo lincoln jaxon asher theodore carter colton hunter eli nolan
        easton landon cameron connor jaxson adrian evan jace cooper xavier dominic parker brayden ian hayden chase miles max
        declan bentley ryder carson micah everett rowan silas kai finn emmett milo ezra beau axel jasper jude kayden maverick
        ayden brody harrison felix tristan oscar bennett kingston graham elliot elliott ivan ryker
        marcus derek travis trevor shawn darren jared chad brett cody dustin troy devin wesley damian marvin calvin
        curtis dale glenn leon lewis mitchell neil phillip rodney stanley allen andre darius malik terrell tyrone jamal
        bob tom tim jim ben sam dan ken ron ted al ed alex chris matt mike nick steve tony andy josh jake jon will dave rick greg
        jeff joey danny jimmy johnny tommy charlie freddie eddie archie harry ollie alfie theo toby ronnie donnie frankie
        """,
        // English: women
        """
        mary patricia jennifer linda elizabeth barbara susan jessica sarah karen lisa nancy betty margaret sandra ashley kimberly
        emily donna michelle carol amanda dorothy melissa deborah stephanie rebecca sharon laura cynthia kathleen amy angela
        shirley anna brenda pamela emma nicole helen samantha katherine christine debra rachel carolyn janet catherine maria
        heather diane ruth julie olivia joyce virginia victoria kelly lauren christina joan evelyn judith megan andrea cheryl
        hannah jacqueline martha gloria teresa ann anne annie sara madison frances kathryn janice jean abigail alice judy sophia
        julia denise amber doris marilyn danielle beverly isabella theresa diana natalie brittany charlotte marie kayla alexis
        lori ava mia harper amelia ella chloe camila aria scarlett penelope layla riley zoey zoe nora lily eleanor hazel violet
        aurora savannah audrey brooklyn bella claire skylar lucy paisley everly caroline nova genesis emilia kennedy maya willow
        kinsley naomi aaliyah elena ariana allison gabriella alexa sadie hailey eva leah serenity stella vivian autumn quinn
        nevaeh piper ruby athena lydia clara delilah isla jade madelyn josephine valentina sophie ivy emery adeline cora jasmine
        faith eliana luna gianna aubrey addison elise mila grace hope joy rose daisy iris crystal dawn summer may june april
        kate katie jen jenny liz beth meg becky cathy kathy sue pam deb jess jessie abby ellie maddie katy kim tina lynn jo jill
        gina angie sally molly peggy betsy wendy cindy tracy stacy mandy sandy shelly holly carrie connie bonnie vicky
        tiffany erica erin kristen kristin courtney lindsey lindsay vanessa sabrina tara melanie jenna alyssa brianna
        kaitlyn haley mackenzie kelsey shannon bethany leslie monique keisha tamara latoya ebony
        """,
        // Spanish and Portuguese
        """
        carlos luis miguel jorge pedro manuel francisco javier alejandro fernando ricardo eduardo rafael sergio roberto andres
        pablo raul enrique alberto hector ramon arturo cesar guillermo gustavo rodrigo emilio hugo ignacio joaquin julio lorenzo
        marco mario nicolas salvador tomas vicente alfredo alonso antonio armando camilo cristian dario esteban felipe gerardo
        gonzalo jaime jesus leonel marcos mauricio octavio orlando rogelio rolando ruben valentin santiago mateo matteo diego
        leonardo joao tiago rui goncalo duarte afonso
        lucia sofia martina paula valeria ximena daniela gabriela carmen rosa isabel adriana alejandra beatriz carolina catalina
        cecilia claudia esperanza fernanda guadalupe ines irene juana leticia lorena luz margarita mariana marta mercedes monica
        natalia pilar raquel rocio silvia susana veronica yolanda leonor
        """,
        // South Asian
        """
        aarav aditya akash amit anil anand arjun arun ashok deepak dev dinesh gaurav harsh karan krishna kunal manish mohit
        nikhil pradeep prakash pranav rahul raj rajesh rakesh ravi rohit sachin sandeep sanjay santosh siddharth sunil suresh
        tarun varun vijay vikram vinay vishal yash abhishek ajay akshay alok ankit ankur ashish ganesh hari kiran mahesh naveen
        nitin pankaj rohan sameer saurabh shiv shyam sumit vivek
        aanya aditi ananya anjali anita anu deepa divya gita isha jaya kavita kriti lakshmi meera neha nisha pooja preeti priya
        radha rani rekha riya sakshi sangeeta shreya shruti sneha sonia sunita swati tanvi usha vandana zara aishwarya ishita
        kavya madhuri nandini pallavi payal ritu
        """,
        // Arabic, Persian, Turkish
        """
        ahmed ahmad ali omar hassan hussein mohammed muhammad mohamed mahmoud mustafa khalid ibrahim yusuf youssef abdullah abdul
        tariq karim hamza bilal faisal amir imran rashid samir sami walid zaid adnan anwar farid jamal nasser salman waleed yasin
        reza dariush mehmet emre burak cem
        fatima aisha amina leila maryam mariam noor nour yasmin zainab zahra hana huda khadija salma samira soraya laila farah
        dina lina rana reem elif zeynep
        """,
        // East and Southeast Asian
        """
        wei jing ming hui yan xin lei hao jie tao fang ling ping qiang yong hong xia mei jian feng gang chao bin hua ying
        xiaoming xiaohong zhiwei jiahui yuxuan zihan haoran yichen li yu bo na yi lan jun
        hiroshi takashi kenji akira yuki haruto sota yuto riku kaito ren daiki takumi kazuki shota naoki ryota satoshi tetsuya
        yusuke koji makoto sakura yui hina aoi yuna mio rin akari himari miyu haruka nanami ayaka misaki yoko keiko emi aiko
        yumi kaori minjun seojun jiwoo jiho hyun junho jisoo minji seoyeon jiyeon soyeon eunji jieun hyejin sooyoung donghyun
        sungmin jaehyun thanh minh linh huong anh
        """,
        // European
        """
        dmitri dmitry sergei sergey alexei alexey andrei andrey mikhail nikolai vladimir yuri boris pavel oleg igor maxim artem
        anton denis viktor konstantin olga natasha tatiana svetlana irina ekaterina anastasia yulia oksana ksenia polina darya
        alina marina galina ludmila piotr krzysztof tomasz pawel marcin michal lukasz jakub kamil agnieszka katarzyna
        malgorzata joanna magdalena monika aleksandra zofia jan hans klaus jurgen wolfgang stefan andreas thorsten matthias
        florian lukas jonas tobias niklas moritz johannes sven lars anders erik nils bjorn olaf magnus henrik mikkel soren
        pieter jens daan sanne femke ingrid astrid sigrid freya greta heidi helga ilse anke birgit petra ursula sabine katrin
        lena leonie nina pierre jacques michel philippe alain francois julien antoine mathieu guillaume thibault benoit olivier
        laurent sylvain cedric damien remi baptiste clement amelie camille manon lea juliette margaux elodie aurelie celine
        sylvie nathalie isabelle sandrine veronique brigitte genevieve giuseppe giovanni francesco alessandro luca davide simone
        federico riccardo stefano paolo fabio massimo giorgio vincenzo chiara giulia francesca alessia federica elisa giorgia
        sean seamus niamh siobhan aoife ciara saoirse roisin oisin cian eoin padraig fionn
        """,
        // African
        """
        kwame kofi kwesi ama akosua abena chidi chinedu emeka ngozi chiamaka adaeze oluwaseun olumide tunde babatunde ayodele
        temitope funmilayo folake segun femi kehinde taiwo wanjiru wambui kamau njeri otieno achieng thabo sipho themba lerato
        nomvula zanele amara zuri imani jabari baraka
        """,
        // More Arabic and Persian
        """
        nabil hamid hamed hamad majid majed mansour mansoor marwan mazen murad
        nasir naser osama usama rami rayan saad saeed saif salem saleh sultan talal tamer turki wael yahya yasser yazan ziad
        zayed abdulaziz abdulrahman abdallah ashraf ayman bassam fadi fahad fahd firas ghassan hadi haitham hakim hani haroon
        harun hatem hazem jawad kareem khaled latif louay maher mahdi mohsen mounir munir naji nizar qais rashed riyad shadi
        tawfiq wassim yousef zakaria arash babak behnam kian mehdi navid omid payam ramin saeid shahin siamak
        noura nadia rania reham rasha ruba rula shaima sherine suha wafa yara zeina dalia ghada hala haya iman jumana
        lamya lubna manal maha mona nada nawal nesrine rawan sawsan shatha alaa basma ebtisam azadeh golnaz mahsa negar parisa
        shirin yasaman
        """,
        // More South Asian
        """
        asif atif danish faraz farhan fawad haris irfan junaid kamran kashif khurram mohsin nadeem naveed noman rizwan sajid
        shahid shoaib tahir umair usman waqar waseem zafar zubair aamir aamer adeel arslan rehan zeeshan
        ayesha bushra farzana mahnoor maira rabia saima shazia sidra tahira uzma fariha nimra anam
        """,
        // More Irish and Scottish
        """
        ciaran conor colm darragh donal dermot fergal killian ronan ruairi tadhg cormac diarmuid lorcan senan
        aisling ailbhe caoimhe clodagh deirdre eimear grainne maeve muireann orla sinead aoibheann ailsa morag eilidh
        """,
        // Pacific Islander
        """
        sione tevita viliami siaosi semisi tomasi ioane iosefa sefo tavita mosese pulu
        malia mele lupe losa sina leilani kalani keola makoa nohea moana mahina
        """,
        // More Spanish and Portuguese
        """
        agustin alvaro benicio cristobal efrain eliseo fabian facundo gael hernan horacio ismael jacinto leandro marcelo
        maximiliano nahuel osvaldo patricio renato saul teodoro ulises
        agustina antonella camila constanza dolores elena florencia jimena josefina lourdes luciana macarena milagros paloma
        romina soledad valentina yesenia gianna gianluca
        """,
        // More Slavic and Central European
        """
        aleksandr aleksei bogdan dariusz goran grzegorz marek miroslav nikola ognjen petar radek tomislav vaclav zoran jiri
        ondrej lubos dusan branko dragan
        ivana jelena katarina lidia vesna zuzana lenka jana marketa bozena dragana milica snezana
        """,
        // More East and Southeast Asian
        """
        hiroko kenta kota masato shinji tomoko yuko xiao xiaoli zhen zhihao junjie yifan yuting jiayi shuang qing hyunwoo
        seungmin jihoon minho taeyang jiwon duc khanh phuong quang trang tuan thuy hoa
        """,
        // More African
        """
        abdou adama aminata amadou bakary cheikh fatou ibrahima mamadou moussa oumar ousmane seydou chukwuma ikenna obinna
        uchenna nnamdi chioma ifeoma nneka adebayo adewale adeola bukola kunle yetunde kwabena akua efua tendai tafadzwa
        tatenda farai nyasha kudzai thandiwe nandi lindiwe busisiwe
        """,
        // More English: women
        """
        colleen maureen noreen doreen arlene darlene charlene marlene irene elaine lorraine jeanette annette paulette
        claudette bernadette antoinette georgette nanette yvette yvonne simone dominique jeannie jeanne joanne joann joanna
        johanna roxanne suzanne leanne dianne marianne rosemary rosalie rosalind rosanne roseanne rosa rosie rita lillian
        lillie gladys mildred edna ethel thelma vera viola wilma velma alma norma irma gertrude agnes bertha myrtle mabel
        hattie mattie minnie nellie essie lula lucille geraldine pauline jacquelyn madeline madeleine evangeline angelina
        angelica angelique veronica monica rebekah priscilla patrice phyllis lois loretta louise lucinda melinda belinda
        glenda wanda rhonda vivienne miriam muriel marion marian harriet henrietta colette nicolette juliet juliette
        juliana julianne josie jodie jody jolene marcia marsha tricia trisha alicia felicia leticia patsy kristina kristine
        kristy krista christy christie cristina tonya tanya sonya sonia sylvia brittney whitney kayleigh kaylee caitlin
        katelyn kaitlin cassandra cassie alexandra alexandria gabrielle michele rochelle raquel rachael abbey adrienne
        adriana alana alanna alison allyson alisha annabelle annabel arabella beatrice becca bernice beverley bridget
        brigitte bronwyn camille candace candice carla carly carmen carole cecilia celeste celia charmaine chantal cheri
        cherie clarissa claudia constance corinne dana daphne darla deanna debbie delia della delores dolores dena desiree
        dianna dina dixie dottie edith edwina elinor eliza ellen eloise elsa elsie elvira emilie erika esmeralda estelle
        esther etta eugenia eunice faye felicity fiona francesca francine freda frieda georgina genevieve gemma gillian
        ginny giselle gwen gwendolyn heidi helena hilary hillary imogen ingrid isabel isabelle isobel jackie janelle janette
        janie janine jeannette jemima jennie jillian jocelyn juanita justine kara karin karina katharine katrina kendra
        kerry kirsten krystal lacey leigh lena leona leonora lesley liza lola lorena lorna lottie louisa lucia luella
        lynette lynda mae maggie mamie marcella margarita margery marguerite mariah marianna maribel marisa marissa marjorie
        marla martina maryann matilda maude maxine meghan meredith michaela millie mindy miranda mollie nadia nadine natalia
        nell nettie nichole nikki noelle nola odessa ophelia paige patti paula petra philippa polly rachelle ramona reba
        regina renee rhoda roberta robyn rosetta sallie selena selma serena shana shanna shauna shawna sheila shelby sheri
        sherri sherry sondra stacey staci susanna susannah tabitha tami tammy tania teri terri tessa thea tonia tracey
        trudy ursula valerie verna vickie wilhelmina winifred zelda
        """,
        // More English: men
        """
        abe abraham alfred alvin ambrose amos andre angelo archibald arturo barry bernard bert bertram blake bradley brady
        brendan brent brett brock bruno bryce byron calvin cameron cecil cedric chad chester clarence claude clayton clifford
        clifton clint clinton clyde cody colin conrad cornelius craig curtis cyril damian damon darrell darren darryl dave
        derek derrick desmond dewey dexter dominic donovan doug duane dudley duncan dustin dwayne dwight edgar edmund edwin
        elbert eldon elias elliot elliott elmer elton elvis emanuel emil emmett enrique ernest ernie errol erwin everett ezra
        felipe fletcher floyd forrest francis franklin fred frederick garrett gavin geoffrey gerard gilbert glenn gregg gus
        gustavo harlan harley harvey hector herbert herman homer horace hubert hugh hugo ian irving irwin isaiah ivan jake
        jared jarvis jay jeffery jerome jimmie joel johnnie jonah josh julius karl kelvin kendall kent kirk kurt lamar lance
        leland lenny leon leonard leroy lester lionel lloyd lonnie lorenzo lowell lyle malcolm manny marc marcus mario
        marlon marty marvin maurice melvin merle micheal mickey milo monroe morris moses myron neal neil nelson noel norman
        orville otis otto percy phil pierre preston quentin ramon randall randolph reginald rene ricky rocco rodney roland
        rolando ronnie rudolph rudy rufus rupert russ sammy saul seth shane shaun shawn sheldon sherman sidney silas simon
        solomon stanley stefan stewart stuart sylvester terrance terrence thaddeus tobias todd tony travis trevor troy
        tyrone ulysses vernon victor virgil wesley wilbur wilfred willard winston woodrow xavier zane zack
        jordan wade glen
        """,
        // Everyday words that are also first names; listed in Ambiguous too, so they need a surname
        """
        laurel olive sierra geneva flora salvador
        """);

    /// <summary>First names that are also everyday words, places or brands; these need a surname next to them.</summary>
    public static readonly HashSet<string> Ambiguous = Set(
        """
        will may june april august mark bill grace hope joy faith rose lily daisy iris ruby amber crystal dawn summer autumn sky
        rich frank chase hunter pat sue don guy jack max ray rob angel destiny harmony star penny holly ivy jade sandy dean drew
        miles carol nick paris florence georgia virginia austin victoria chelsea brooklyn madison lincoln jackson savannah
        carolina santiago mercedes nova genesis luna aurora willow serenity quinn piper hazel violet stella everly kennedy parker
        cooper hudson carter mason harrison bentley ryder carson maverick kingston easton axel beau jasper rowan finn felix oscar
        leo jesus li yu bo na yi lan jun hana mei rin aoi jan dev hari raj shiv luz ana aria alexa zara harsh kai paisley ted
        al ed jo deb kim ann sam tom ben dan ken ron tim jim bob jon sara anna emi linh anh minh ren gita lena nina elise
        sultan salem kota sina moana nada maha hala danish
        jordan wade glen laurel olive sierra geneva flora salvador petra regina kent troy chester
        """);

    /// <summary>Common family names. Surnames that are also everyday words (Black, King, Price, Page…) are left out.</summary>
    public static readonly HashSet<string> Surnames = Set(
        """
        smith johnson williams jones garcia miller davis rodriguez martinez hernandez lopez gonzalez wilson anderson thomas taylor
        lee pham hoang huynh nakamura
        moore martin thompson harris sanchez clark ramirez lewis robinson walker allen wright scott torres nguyen flores adams
        nelson baker rivera campbell mitchell carter roberts perez collins stewart morris rogers reed morgan cooper richardson cox
        howard ward peterson watson brooks sanders bennett barnes ross henderson coleman jenkins perry powell patterson hughes
        washington butler simmons foster bryant alexander russell griffin diaz hayes myers hamilton graham sullivan wallace
        owens reynolds fisher ellis harrison gibson mcdonald cruz marshall ortiz gomez murray freeman webb simpson stevens tucker
        porter hicks crawford henry boyd mason kennedy warren dixon ramos reyes burns gordon shaw robertson daniels palmer
        nichols knight ferguson hawkins dunn perkins hudson spencer gardner stephens payne pierce berry matthews arnold wagner
        willis watkins olson carroll duncan snyder hart cunningham bradley andrews ruiz harper riley armstrong carpenter weaver
        greene lawrence elliott chavez austin peters kelley franklin lawson gutierrez schmidt carr vasquez castillo wheeler
        chapman oliver montgomery richards williamson johnston meyer mccoy howell alvarez morrison hansen fernandez garza harvey
        burton stanley george jacobs reid fuller lynch gilbert garrett romero welch larson frazier burke hanson mendoza moreno
        bowman medina fowler brewer hoffman carlson silva pearson douglas fleming jensen vargas byrd davidson hopkins terry
        herrera soto walters curtis neal caldwell lowe jennings barnett jimenez horton shelton barrett castro sutton gregory
        mckinney lucas craig lambert fletcher watts bates hale rhodes pena newman haynes mcdaniel mendez vaughn dawson santiago
        norris hardy schultz barker guzman munoz keller chandler weber leonard walsh lyons ramsey wolfe schneider mullins benson
        bowen barber cummings hines baldwin griffith valdez hubbard salazar reeves warner stevenson burgess santos tate garner
        thornton dennis mcgee delgado aguilar vega glover manning cohen harmon rodgers robbins todd blair higgins ingram
        reese strickland townsend goodwin walton rowe ortega patton swanson joseph francis goodman maldonado yates becker
        erickson hodges rios conner adkins webster norman malone hammond cobb moody quinn blake maxwell osborne mccarthy guerrero
        lindsey estrada sandoval gibbs fitzgerald stokes doyle sherman saunders colon gill alvarado greer padilla simon
        nunez ballard schwartz mcbride christensen klein pratt briggs parsons mclaughlin zimmerman buchanan moran copeland
        pittman brady mccormick holloway brock poole logan owen drake wong jefferson morton abbott patrick huff clayton
        massey lloyd figueroa carson bowers roberson barton tran harrington casey boone cortez clarke mathis singleton wilkins
        cain bryan underwood hogan mckenzie collier luna phelps mcguire allison bridges wilkerson nash summers atkins o'brien
        murphy kelly ryan byrne mccarthy o'connor o'neill doherty kennedy gallagher
        patel singh kumar sharma gupta shah mehta reddy rao nair iyer menon pillai joshi desai chopra kapoor malhotra verma
        agarwal banerjee chatterjee mukherjee khan ahmed hussain hassan rahman siddiqui qureshi chaudhry malik
        wang zhang liu chen yang huang zhao zhou sun zhu guo luo liang song zheng xie deng feng cheng cao yuan
        sato suzuki takahashi tanaka watanabe ito yamamoto nakamura kobayashi kato yoshida yamada sasaki yamaguchi matsumoto inoue
        choi jung kang cho yoon jang lim
        muller mueller schmidt schneider fischer weber meyer wagner becker schulz hoffmann koch richter klein schroeder
        rossi russo ferrari esposito bianchi romano ricci marino greco bruno gallo conti costa giordano rizzo lombardi
        dubois durand leroy moreau laurent lefebvre bernard petit girard bonnet fontaine rousseau
        oliveira souza pereira ferreira almeida gomes ribeiro carvalho fernandes rocha
        ivanov smirnov kuznetsov popov petrov sokolov volkov kowalski nowak wisniewski wojcik kaminski lewandowski jansen
        vries dijk bakker visser janssen hansen nielsen andersen larsen johansson andersson karlsson nilsson eriksson
        larsson olsen
        okafor okonkwo adeyemi ogunleye mensah owusu boateng asante kamau otieno mwangi ndlovu dlamini nkosi
        velazquez navarro dominguez serrano molina morales cabrera fuentes herrero lozano marquez mejia montoya
        ochoa pacheco paredes quintero rubio sosa suarez trujillo valencia zamora
        haddad khoury nassar mansour hamdan khalil aziz rahim karimi hosseini ahmadi mohammadi rezaei akhtar aslam baig
        iqbal javed mirza naqvi rizvi sheikh zaidi
        hartmann werner lehmann schmitt krause meier zimmermann braun hofmann vogel friedrich
        novak horvat kovac kovacevic jovanovic petrovic nikolic markovic popovic dvorak novotny svoboda
        fifita tupou faleolo tuilagi leota sopoaga fonoti
        o'sullivan o'reilly o'donnell mcmahon mcgrath brennan fitzpatrick
        alvarez castillo chavez jimenez moreno ruiz romero herrera medina aguilar vargas castro guzman mendoza salazar
        fernandez delgado vasquez vazquez soto contreras estrada figueroa espinoza sandoval guerrero rios dominguez cortez
        juarez molina silva acosta nunez navarro campos ibarra maldonado mejia cervantes calderon cabrera rojas meza solis
        orozco serrano pena munoz valdez alvarado padilla marquez ayala gallegos trevino salinas montoya guerra carrillo
        velasquez mercado camacho rivas cardenas cantu fuentes salas santana rosales macias valenzuela acevedo barrera
        castaneda velazquez villarreal melendez rosario aguirre franco nieves rocha robles duran avila
        murphy kelly bailey richards watkins olson carroll duncan snyder cunningham bradley andrews harper riley
        armstrong greene lawrence elliott peters kelley lawson gutierrez ryan schmidt carr wheeler chapman oliver
        montgomery williamson johnston meyer mccoy howell morrison hansen garza harvey burton stanley george jacobs reid
        fuller lynch gilbert garrett welch larson frazier burke hanson bowman fowler hoffman carlson pearson douglas fleming
        jensen byrd davidson hopkins terry wade walters curtis neal caldwell lowe jennings barnett horton shelton barrett
        obrien sutton gregory mckinney lucas craig holt lambert fletcher bates hale rhodes beck newman haynes mcdaniel mendez
        vaughn dawson norris barker schultz keller chandler weber leonard walsh lyons ramsey wolfe schneider mullins benson
        bowen daniel cummings hines baldwin griffith hubbard reeves stevenson burgess santos tate garner thornton dennis
        mcgee glover manning cohen harmon rodgers robbins todd blair higgins ingram reese strickland townsend goodwin
        walton rowe ortega patton swanson joseph francis goodman yates becker erickson hodges conner adkins webster
        norman malone hammond cobb blake maxwell floyd osborne paul mccarthy lindsey tyler fitzgerald stokes doyle sherman
        saunders alvarado greer ballard schwartz mcbride christensen klein pratt briggs parsons mclaughlin zimmerman
        buchanan moran copeland roy pittman brady mccormick holloway brock poole logan owen jefferson morton abbott patrick
        clayton massey lloyd carson bowers roberson barton tran harrington casey boone clarke mathis singleton
        wilkins bryan underwood hogan mckenzie collier phelps mcguire allison wilkerson nash atkins wilcox pitts conley
        burnett cochran davenport sawyer dickerson hodge flynn nicholson monroe morrow kirk randall anthony whitaker oconnor
        skinner kirby huffman gilmore oneal bruce kramer hancock gallagher gaines shaffer wiggins mathews mcclain
        fischer melton hensley dyer cameron grimes wyatt baxter mosley larsen hoover beasley glenn petersen whitehead meyers
        vincent olsen schroeder woodard mueller kemp deleon calhoun wiley eaton cline harrell humphrey parrish
        hutchinson hess dorsey bullock dalton vance blackwell blankenship pruitt callahan hardin mcdowell carey stafford
        henson wilkinson merritt atkinson decker hobbs knox stephenson hickman mcclure conway roth maynard
        farrell lowery hurst nixon weiss ellison sloan mclean randolph boyer mccall ayers leblanc browning
        chang foley bartlett landry bauer everett bradshaw pugh velez estes dodson morse
        sheppard barron livingston middleton blevins chen kerr mcconnell hatfield harding giles blackburn pennington
        woodward finley mcintosh koch mccullough nolan blanchard kane benton buckley haley maddox russo mcknight mcmillan
        crosby dotson faulkner oneill knapp kline ochoa jacobson avery hendricks horne shepard hebert mcintyre
        waller holman donaldson terrell morin gillespie tillman sanford rollins dickson howe hurley spence yang mcneil
        sampson carver macdonald melendez dillon farley galloway joyner aguirre osborn mercer rowland pickett
        dunlap hayden mckay coffey mccarty ewing cooley vaughan bonner ferrell cantrell fulton pollard hooper burch
        mullen odonnell daugherty berger dillard alston jarvis frye riggs chaney odom duffy merrill mayer alford mcpherson
        donovan mooney mcgowan clemons wynn nielsen baird stanton snider witt stuart holden rutledge kinney
        clements slater hahn emerson conrad delaney tyson whitfield talley irwin burris ratliff mccray madden
        kaufman mcfadden levine byers kirkland kidd carney mcleod holcomb hendrix sosa haney sargent rasmussen hewitt
        gilliam guthrie wooten huber barlow boyle buckner puckett langley knowles cooke whitley
        wang zhang liu huang zhao zhou xu sun zhu guo lin luo zheng liang xie feng deng cao peng zeng xiao tian
        yuan cai jiang cheng su shen yao choi jung kang cho yoon jang lim shin kwon hwang ahn ryu jeon
        sato suzuki takahashi tanaka watanabe ito yamamoto kobayashi kato yoshida yamada sasaki yamaguchi matsumoto inoue
        kimura hayashi shimizu yamazaki mori ikeda hashimoto ishikawa ogawa okada hasegawa murakami kondo ishii saito
        sakamoto endo aoki fujita nishimura fukuda miura fujii okamoto matsuda nakagawa nakano harada tamura takeuchi kaneko
        wada nakayama ishida ueda morita shibata sakai kudo yokoyama miyazaki miyamoto uchida takagi ando taniguchi
        phan dang bui ngo duong
        sharma verma gupta singh kumar shah mehta joshi desai reddy rao nair iyer menon pillai chatterjee banerjee
        mukherjee ghosh dutta chakraborty kapoor khanna malhotra arora bhatia chopra sethi saxena agarwal bansal goel mittal
        tiwari pandey mishra dubey yadav chauhan thakur rathore naidu krishnan subramanian srinivasan natarajan
        ahmed mohamed mohammed hassan hussein ibrahim mahmoud mustafa abdullah rahman hamid saleh farouk nasser qureshi
        siddiqui ansari chaudhry
        schulz richter neumann schwarz kruger lange schmid schulze maier kohler herrmann fuchs scholz moller
        schubert winkler lorenz baumann franke albrecht schuster ludwig bohm kraus schumacher vogt seidel heinrich brandt
        haas schreiber dietrich ziegler kuhn pohl bergmann voigt pfeiffer
        dubois petit durand leroy moreau laurent lefebvre bertrand fournier morel girard lefevre mercier dupont bonnet
        legrand garnier faure rousseau guerin roussel perrin gauthier dumont fontaine chevalier masson lemaire duval joly
        gautier meunier marchand dufour barbier brunet leroux renard arnaud rolland caron aubert giraud leclerc vidal
        renaud lemoine picard gaillard leclercq lacroix dupuis lecomte menard deschamps benoit maillard
        rossi ferrari esposito bianchi romano ricci marino greco giordano mancini rizzo lombardi moretti barbieri
        fontana santoro mariani rinaldi caruso ferrara galli martini longo gentile martinelli vitale lombardo coppola
        marchetti parisi conte ferraro fabbri marini grasso valentini gatti pellegrini palumbo sanna farina rizzi
        cattaneo morelli amato silvestri giuliani benedetti barone caputo montanari palmieri bernardi ferretti bellini
        nowak kowalski wisniewski wojcik kowalczyk kaminski lewandowski zielinski szymanski wozniak dabrowski kozlowski
        jankowski mazur kwiatkowski krawczyk piotrowski grabowski pawlowski michalski nowicki adamczyk wieczorek jablonski
        majewski olszewski jaworski malinowski pawlak witkowski walczak sikora ostrowski tomaszewski marciniak zalewski
        ivanov smirnov kuznetsov popov vasiliev petrov sokolov mikhailov novikov fedorov morozov volkov alekseev lebedev
        semenov egorov pavlov kozlov stepanov nikolaev orlov andreev makarov nikitin zakharov zaitsev borisov yakovlev
        romanov sergeev kuzmin frolov dmitriev korolev gusev kiselev maksimov polyakov sorokin vinogradov kovalev belov
        medvedev antonov tarasov zhukov baranov filippov komarov davydov bogdanov osipov sidorov ivanova smirnova
        kuznetsova popova petrova sokolova volkova novikova morozova
        """);

    /// <summary>Words that put a person right after them: "emailed Maelis Ondrako", "Hi Oriel", "cc Tamsin Vell".</summary>
    public static readonly HashSet<string> Cues = Set(
        """
        emailed e-mailed called phoned texted messaged pinged dmed invited thanked met cc'd cced mentioned tagged hi hello hey
        dear thanks cc bcc
        """);

    /// <summary>Verbs that put a person right before them: "Darvin Kesselring replied".</summary>
    public static readonly HashSet<string> SaidVerbs = Set("emailed e-mailed replied wrote commented said called texted messaged posted shared");

    /// <summary>Words that make the name after them a place: "San Francisco", "Fort Lauderdale", "St Helena".</summary>
    public static readonly HashSet<string> PlaceWords = Set("san santa santo sao são saint st ste fort port mount mt lake cape");

    /// <summary>Lowercase words inside family names: "Robles de la Fuente", "van der Berg", "bin Hamdan".</summary>
    public static readonly HashSet<string> Particles = Set("de la las los del della di da das do dos du le van von der den ter ten bin bint binti al el abu ibn y");

    /// <summary>Words that end a name: "Aumalo Jr", "Ward III".</summary>
    public static readonly HashSet<string> Suffixes = Set("jr sr ii iii iv v vi");

    /// <summary>Column headers whose cells are people's names.</summary>
    public static readonly HashSet<string> NameHeaders = Set(
        """
        name names customer user contact member attendee owner employee person assignee author recipient sender client patient
        student guest participant speaker candidate applicant reviewer requester reporter host organizer teammate agent
        """);

    /// <summary>Capitalized words that never start or continue a name: grammar, dates, and interface words.</summary>
    public static readonly HashSet<string> Stop = Set(
        """
        the a an and or but of in on at to for from with by as is are was were be been has have had do does did not no yes ok okay
        please thanks thank hi hello hey dear re fwd fw cc bcc subject sent date today yesterday tomorrow
        monday tuesday wednesday thursday friday saturday sunday mon tue tues wed thu thur thurs fri sat sun
        january february march april may june july august september october november december
        jan feb mar apr jun jul aug sep sept oct nov dec
        inc llc ltd corp co team support admin i me you we they he she it this that these those what when where who why how
        which if then so just also now here there about more most some any each every other another such only own same than too
        very can will should would could might must shall all my your our their his her its
        settings file edit view help home search new open save close cancel delete send reply forward total price account user
        users name names contact contacts profile member members group groups project projects page pages street avenue road city
        state county university college school hospital center centre park lake mount saint st san santa north south east west old big
        little great good best first last next previous show hide add remove update download upload share copy paste print
        export import sign log login logout out up down left right top bottom select none less read unread done complete
        completed archive archived spam junk flag flagged pin pinned mute block report follow following followers like likes
        comment comments message messages chat call join leave invite create start stop play pause back
        google microsoft apple amazon windows office word excel outlook teams chrome edge firefox safari slack zoom discord
        github visual studio code notes mail calendar photos music video videos news weather store maps system control panel
        manager explorer terminal desktop documents downloads pictures recent favorites library tools options preferences general
        privacy security network internet wifi bluetooth display sound battery storage apps features accounts time language
        gaming accessibility version build release beta alpha pro plus premium free trial buy sale deal order orders cart
        checkout shipping delivery payment invoice receipt balance amount due paid status pending active inactive online offline
        available busy away
        """);
}
