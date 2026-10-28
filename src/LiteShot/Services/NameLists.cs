namespace LiteShot;

/// <summary>Word lists behind <see cref="NameFinder"/>. All lowercase.</summary>
internal static class NameLists
{
    static HashSet<string> Set(params string[] groups) =>
        new(groups.SelectMany(g => g.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparer.Ordinal);

    public static readonly HashSet<string> Titles = Set("mr mrs ms miss mx dr prof sir dame madam mme mlle herr frau sr sra srta");

    /// <summary>Common given names from many cultures.</summary>
    public static readonly HashSet<string> FirstNames = Set(
        // English: men
        """
        james john robert michael william david richard joseph thomas charles christopher daniel matthew anthony mark donald
        steven paul andrew joshua kenneth kevin brian george timothy ronald edward jason jeffrey ryan jacob gary nicholas eric
        jonathan stephen larry justin scott brandon benjamin samuel gregory alexander frank patrick raymond jack dennis jerry
        tyler aaron jose adam nathan henry douglas zachary peter kyle ethan walter noah jeremy christian keith roger terry gerald
        harold sean austin carl arthur lawrence dylan jesse jordan bryan billy joe bruce gabriel logan albert willie alan juan
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
        """);

    /// <summary>First names that are also everyday words, places or brands; these need a surname next to them.</summary>
    public static readonly HashSet<string> Ambiguous = Set(
        """
        will may june april august mark bill grace hope joy faith rose lily daisy iris ruby amber crystal dawn summer autumn sky
        rich frank chase hunter pat sue don guy jack max ray rob angel destiny harmony star penny holly ivy jade sandy dean drew
        miles carol nick jordan paris florence georgia virginia austin victoria chelsea brooklyn madison lincoln jackson savannah
        carolina santiago mercedes nova genesis luna aurora willow serenity quinn piper hazel violet stella everly kennedy parker
        cooper hudson carter mason harrison bentley ryder carson maverick kingston easton axel beau jasper rowan finn felix oscar
        leo jesus li yu bo na yi lan jun hana mei rin aoi jan dev hari raj shiv luz ana aria alexa zara harsh kai paisley ted
        al ed jo deb kim ann sam tom ben dan ken ron tim jim bob jon sara anna emi linh anh minh ren gita lena nina elise
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
        greene lawrence elliott chavez sims austin peters kelley franklin lawson gutierrez schmidt carr vasquez castillo wheeler
        chapman oliver montgomery richards williamson johnston meyer mccoy howell alvarez morrison hansen fernandez garza harvey
        burton stanley george jacobs reid fuller lynch gilbert garrett romero welch larson frazier burke hanson mendoza moreno
        bowman medina fowler brewer hoffman carlson silva pearson douglas fleming jensen vargas byrd davidson hopkins terry
        herrera soto walters curtis neal caldwell lowe jennings barnett jimenez horton shelton barrett castro sutton gregory
        mckinney lucas craig lambert fletcher watts bates hale rhodes pena newman haynes mcdaniel mendez vaughn dawson santiago
        norris hardy schultz barker guzman munoz keller chandler weber leonard walsh lyons ramsey wolfe schneider mullins benson
        bowen barber cummings hines baldwin griffith valdez hubbard salazar reeves warner stevenson burgess santos tate garner
        thornton dennis mcgee delgado aguilar vega glover manning cohen harmon rodgers robbins newton todd blair higgins ingram
        reese strickland townsend goodwin walton rowe hampton ortega patton swanson joseph francis goodman maldonado yates becker
        erickson hodges rios conner adkins webster norman malone hammond cobb moody quinn blake maxwell osborne mccarthy guerrero
        lindsey estrada sandoval gibbs fitzgerald stokes doyle sherman saunders colon gill alvarado greer padilla simon
        nunez ballard schwartz mcbride christensen klein pratt briggs parsons mclaughlin zimmerman buchanan moran copeland
        pittman brady mccormick holloway brock poole logan owen drake wong jefferson morton abbott patrick norton huff clayton
        massey lloyd figueroa carson bowers roberson barton tran harrington casey boone cortez clarke mathis singleton wilkins
        cain bryan underwood hogan mckenzie collier luna phelps mcguire allison bridges wilkerson nash summers atkins o'brien
        murphy kelly ryan byrne mccarthy o'connor o'neill doherty kennedy gallagher
        patel singh kumar sharma gupta shah mehta reddy rao nair iyer menon pillai joshi desai chopra kapoor malhotra verma
        agarwal banerjee chatterjee mukherjee bose khan ahmed hussain hassan rahman siddiqui qureshi chaudhry malik
        wang zhang liu chen yang huang zhao zhou sun zhu guo luo liang song zheng xie tang deng feng cheng cao yuan
        sato suzuki takahashi tanaka watanabe ito yamamoto nakamura kobayashi kato yoshida yamada sasaki yamaguchi matsumoto inoue
        choi jung kang cho yoon jang lim
        muller mueller schmidt schneider fischer weber meyer wagner becker schulz hoffmann koch richter klein schroeder
        rossi russo ferrari esposito bianchi romano colombo ricci marino greco bruno gallo conti costa giordano rizzo lombardi
        dubois durand leroy moreau laurent lefebvre bernard petit girard bonnet fontaine rousseau
        oliveira souza pereira ferreira almeida gomes ribeiro carvalho fernandes rocha
        ivanov smirnov kuznetsov popov petrov sokolov volkov kowalski nowak wisniewski wojcik kaminski lewandowski jansen
        vries dijk bakker visser janssen hansen nielsen andersen larsen johansson andersson karlsson nilsson eriksson
        larsson olsen
        okafor okonkwo adeyemi ogunleye mensah owusu boateng asante kamau otieno mwangi ndlovu dlamini nkosi
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
        state county university college school hospital center centre park lake mount saint st north south east west old big
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
